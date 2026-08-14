using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class KsefApiClient : IDisposable
{
    // API dopuszcza maksymalnie trzy miesiące. Dwumiesięczne okna zostawiają
    // bezpieczny margines na zmianę czasu i różne offsety strefy Warszawy.
    private const int MetadataQueryWindowMonths = 2;
    private static readonly TimeSpan DefaultMetadataRequestInterval = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly HttpMessageHandler _handler;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _metadataPacingGate = new(1, 1);
    private readonly TimeSpan _metadataRequestInterval;
    private readonly Func<CancellationToken, Task>? _metadataRequestReservation;
    private DateTimeOffset _lastMetadataRequestUtc;
    private readonly string _nip;
    private string _ksefToken;
    private TokenInfo? _accessToken;
    private TokenInfo? _refreshToken;

    public KsefApiClient(
        AppSettings settings,
        string ksefToken,
        HttpMessageHandler? handler = null,
        TimeSpan? metadataRequestInterval = null,
        Func<CancellationToken, Task>? metadataRequestReservation = null)
    {
        _nip = NipValidator.Normalize(settings.Nip);
        _ksefToken = ksefToken.Trim();
        _metadataRequestInterval = metadataRequestInterval ?? DefaultMetadataRequestInterval;
        _metadataRequestReservation = metadataRequestReservation;
        ArgumentOutOfRangeException.ThrowIfLessThan(_metadataRequestInterval, TimeSpan.Zero);
        _handler = handler ?? CreateDefaultHandler();
        _http = new HttpClient(_handler, disposeHandler: false);
        _http.BaseAddress = AppSettings.GetBaseUri();
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInformation.UserAgent);
        _http.DefaultRequestHeaders.Add("X-Error-Format", "problem-details");
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task<MetadataQueryResult> QueryReceivedInvoicesAsync(
        DateTimeOffset from,
        CancellationToken cancellationToken)
    {
        var collected = new Dictionary<string, InvoiceMetadata>(StringComparer.Ordinal);
        DateTimeOffset? hwm = null;
        var windowFrom = from;

        for (var windowGuard = 0; windowGuard < 100; windowGuard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximumWindowEnd = windowFrom.AddMonths(MetadataQueryWindowMonths);
            var windowTo = maximumWindowEnd < DateTimeOffset.UtcNow ? maximumWindowEnd : (DateTimeOffset?)null;
            var window = await QueryReceivedInvoicesWindowAsync(windowFrom, windowTo, cancellationToken).ConfigureAwait(false);

            foreach (var invoice in window.Invoices)
                collected[invoice.KsefNumber] = invoice;
            if (window.PermanentStorageHwmDate is { } windowHwm) hwm = windowHwm;

            if (windowTo is null)
                return new MetadataQueryResult(collected.Values.OrderBy(x => x.PermanentStorageDate).ToList(), hwm);

            // Okna są przylegające. Rekord na granicy może wrócić ponownie,
            // dlatego wynik jest deduplikowany powyżej po numerze KSeF.
            windowFrom = windowTo.Value;
        }

        throw new KsefApiException("Przekroczono bezpieczny limit okien czasowych odpowiedzi KSeF.");
    }

    private async Task<MetadataQueryResult> QueryReceivedInvoicesWindowAsync(
        DateTimeOffset from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var collected = new Dictionary<string, InvoiceMetadata>(StringComparer.Ordinal);
        DateTimeOffset? hwm = null;
        var pageOffset = 0;
        var rangeFrom = from;
        var completed = false;

        for (var guard = 0; guard < 200; guard++)
        {
            var body = CreateReceivedInvoiceQuery(rangeFrom, to);

            using var response = await SendAuthorizedAsync(
                HttpMethod.Post,
                $"invoices/query/metadata?sortOrder=Asc&pageOffset={pageOffset}&pageSize=250",
                body,
                cancellationToken,
                isMetadataRequest: true).ConfigureAwait(false);

            using var document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            ValidateMetadataResponse(root);
            if (TryGetDateTimeOffset(root, "permanentStorageHwmDate", out var responseHwm)) hwm = responseHwm;

            var page = new List<InvoiceMetadata>();
            var invoices = root.GetProperty("invoices");
            foreach (var invoice in invoices.EnumerateArray())
            {
                var parsed = ParseMetadata(invoice);
                if (string.IsNullOrWhiteSpace(parsed.KsefNumber)) continue;
                collected[parsed.KsefNumber] = parsed;
                page.Add(parsed);
            }

            var hasMore = root.GetProperty("hasMore").GetBoolean();
            var truncated = root.GetProperty("isTruncated").GetBoolean();
            if (!hasMore)
            {
                completed = true;
                break;
            }

            if (truncated)
            {
                var lastDate = page.LastOrDefault()?.PermanentStorageDate;
                if (lastDate is null || lastDate <= rangeFrom)
                    throw new KsefApiException("KSeF zwrócił ucięty wynik bez poprawnego punktu kontynuacji.");
                rangeFrom = lastDate.Value;
                pageOffset = 0;
            }
            else
            {
                pageOffset++;
            }
        }

        if (!completed) throw new KsefApiException("Przekroczono bezpieczny limit stron odpowiedzi KSeF.");
        return new MetadataQueryResult(collected.Values.OrderBy(x => x.PermanentStorageDate).ToList(), hwm);
    }

    public async Task VerifyInvoiceReadAccessAsync(CancellationToken cancellationToken)
    {
        // To jest wyłącznie test uprawnienia InvoiceRead, nie punkt synchronizacji.
        // Wyłączenie ograniczenia HWM zapobiega fałszywemu błędowi 21183, gdy
        // bieżący HWM serwera jest opóźniony o więcej niż pięć minut.
        var body = CreateReceivedInvoiceQuery(DateTimeOffset.UtcNow.AddMinutes(-5), null, restrictToHwm: false);
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "invoices/query/metadata?sortOrder=Asc&pageOffset=0&pageSize=10",
            body,
            cancellationToken,
            isMetadataRequest: true).ConfigureAwait(false);
        using var document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        ValidateMetadataResponse(document.RootElement);
    }

    private async Task WaitForMetadataRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _metadataPacingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastMetadataRequestUtc != default)
            {
                var delay = _lastMetadataRequestUtc + _metadataRequestInterval - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            if (_metadataRequestReservation is not null)
                await _metadataRequestReservation(cancellationToken).ConfigureAwait(false);
            _lastMetadataRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _metadataPacingGate.Release();
        }
    }

    public async Task<string> DownloadInvoiceXmlAsync(string ksefNumber, CancellationToken cancellationToken)
    {
        var escaped = Uri.EscapeDataString(ksefNumber);
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"invoices/ksef/{escaped}", null, cancellationToken)
            .ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xml))
            throw new KsefApiException("KSeF zwrócił pustą treść faktury.");
        return xml;
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } access && access.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(1)) return;

        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } current && current.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(1)) return;

            if (_refreshToken is { } refresh && refresh.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                try
                {
                    using var request = CreateRequest(HttpMethod.Post, "auth/token/refresh");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refresh.Token);
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                    using var json = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
                    _accessToken = ParseTokenInfo(json.RootElement.GetProperty("accessToken"));
                    return;
                }
                catch (KsefApiException exception) when (ShouldStartFullAuthentication(exception.StatusCode))
                {
                    // Pełny challenge ma sens wyłącznie wtedy, gdy refresh token
                    // został odrzucony. Błędy chwilowe (429/5xx) muszą trafić do
                    // harmonogramu razem z Retry-After; wykonanie w tym miejscu
                    // kolejnych 5+ żądań tylko pogłębiłoby przeciążenie KSeF.
                    _accessToken = null;
                    _refreshToken = null;
                }
            }

            await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private static bool ShouldStartFullAuthentication(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private async Task AuthenticateCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ksefToken)) throw new KsefApiException("Nie zapisano tokena KSeF.");

        var certificate = await GetEncryptionCertificateAsync("KsefTokenEncryption", cancellationToken).ConfigureAwait(false);

        using var challengeResponse = await SendPublicAsync(HttpMethod.Post, "auth/challenge", null, cancellationToken)
            .ConfigureAwait(false);
        using var challengeJson = await ParseJsonAsync(challengeResponse, cancellationToken).ConfigureAwait(false);
        var challenge = GetRequiredString(challengeJson.RootElement, "challenge");
        var timestampMs = challengeJson.RootElement.GetProperty("timestampMs").GetInt64();

        byte[] encrypted;
        using (var x509 = X509CertificateLoader.LoadCertificate(certificate.CertificateDer))
        using (var rsa = x509.GetRSAPublicKey())
        {
            if (rsa is null) throw new KsefApiException("Certyfikat KSeF nie zawiera klucza RSA.");
            var clear = Encoding.UTF8.GetBytes($"{_ksefToken}|{timestampMs}");
            try
            {
                encrypted = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        var authBody = new
        {
            challenge,
            contextIdentifier = new { type = "Nip", value = _nip },
            encryptedToken = Convert.ToBase64String(encrypted),
            publicKeyId = certificate.PublicKeyId
        };
        CryptographicOperations.ZeroMemory(encrypted);

        using var authResponse = await SendPublicAsync(HttpMethod.Post, "auth/ksef-token", authBody, cancellationToken)
            .ConfigureAwait(false);
        using var authJson = await ParseJsonAsync(authResponse, cancellationToken).ConfigureAwait(false);
        var referenceNumber = GetRequiredString(authJson.RootElement, "referenceNumber");
        var authenticationToken = ParseTokenInfo(authJson.RootElement.GetProperty("authenticationToken"));

        var statusCode = 100;
        string statusDescription = "Uwierzytelnianie w toku";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt < 3 ? 1 : 2), cancellationToken).ConfigureAwait(false);
            using var statusRequest = CreateRequest(HttpMethod.Get, $"auth/{Uri.EscapeDataString(referenceNumber)}");
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authenticationToken.Token);
            using var statusResponse = await _http.SendAsync(statusRequest, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(statusResponse, cancellationToken).ConfigureAwait(false);
            using var statusJson = await ParseJsonAsync(statusResponse, cancellationToken).ConfigureAwait(false);
            var status = statusJson.RootElement.GetProperty("status");
            statusCode = status.GetProperty("code").GetInt32();
            statusDescription = GetString(status, "description");
            if (statusCode != 100) break;
        }

        if (statusCode != 200)
            throw new KsefApiException($"Uwierzytelnienie nie powiodło się ({statusCode}): {statusDescription}");

        using var redeemRequest = CreateRequest(HttpMethod.Post, "auth/token/redeem");
        redeemRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authenticationToken.Token);
        using var redeemResponse = await _http.SendAsync(redeemRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(redeemResponse, cancellationToken).ConfigureAwait(false);
        using var redeemJson = await ParseJsonAsync(redeemResponse, cancellationToken).ConfigureAwait(false);
        _accessToken = ParseTokenInfo(redeemJson.RootElement.GetProperty("accessToken"));
        _refreshToken = ParseTokenInfo(redeemJson.RootElement.GetProperty("refreshToken"));
    }

    private async Task<EncryptionCertificate> GetEncryptionCertificateAsync(string usage, CancellationToken cancellationToken)
    {
        using var response = await SendPublicAsync(HttpMethod.Get, "security/public-key-certificates", null, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("usage", out var usages) || usages.ValueKind != JsonValueKind.Array) continue;
            if (!usages.EnumerateArray().Any(x => string.Equals(x.GetString(), usage, StringComparison.OrdinalIgnoreCase))) continue;
            if (TryGetDateTimeOffset(element, "validFrom", out var validFrom) && validFrom > now) continue;
            if (TryGetDateTimeOffset(element, "validTo", out var validTo) && validTo <= now) continue;

            return new EncryptionCertificate(
                Convert.FromBase64String(GetRequiredString(element, "certificate")),
                GetRequiredString(element, "publicKeyId"));
        }

        throw new KsefApiException($"KSeF nie udostępnił aktualnego klucza do operacji {usage}.");
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        CancellationToken cancellationToken,
        bool isMetadataRequest = false)
    {
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (isMetadataRequest)
            await WaitForMetadataRequestSlotAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(method, relativeUri, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken!.Token);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _accessToken = null;
            await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (isMetadataRequest)
                await WaitForMetadataRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using var retry = CreateRequest(method, relativeUri, body);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken!.Token);
            response = await _http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> SendPublicAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, relativeUri, body);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri, object? body = null)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    private static object CreateReceivedInvoiceQuery(
        DateTimeOffset from,
        DateTimeOffset? to,
        bool restrictToHwm = true)
    {
        if (to is { } rangeTo)
        {
            return new
            {
                subjectType = "Subject2",
                dateRange = new
                {
                    dateType = "PermanentStorage",
                    from = from.ToString("O", CultureInfo.InvariantCulture),
                    to = rangeTo.ToString("O", CultureInfo.InvariantCulture),
                    restrictToPermanentStorageHwmDate = restrictToHwm
                }
            };
        }

        return new
        {
            subjectType = "Subject2",
            dateRange = new
            {
                dateType = "PermanentStorage",
                from = from.ToString("O", CultureInfo.InvariantCulture),
                restrictToPermanentStorageHwmDate = restrictToHwm
            }
        };
    }

    private static void ValidateMetadataResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("hasMore", out var hasMore) || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("isTruncated", out var isTruncated) || isTruncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("invoices", out var invoices) || invoices.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("permanentStorageHwmDate", out var hwm) || hwm.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(hwm.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new KsefApiException("KSeF zwrócił niepełną odpowiedź z listą metadanych faktur.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var statusCode = response.StatusCode;
        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var problem = ParseProblem(body);
        var message = problem.Message;
        if (statusCode == HttpStatusCode.TooManyRequests && retryAfter is { } delay)
            message += $" Ponowienie będzie możliwe za około {Math.Ceiling(delay.TotalSeconds):0} s.";
        response.Dispose();
        throw new KsefApiException(
            $"KSeF HTTP {(int)statusCode}: {message}",
            statusCode,
            retryAfter: retryAfter,
            errorCodes: problem.ErrorCodes);
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value?.Date is not { } date) return null;
        var delay = date - DateTimeOffset.UtcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static ParsedProblem ParseProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new ParsedProblem("Brak treści odpowiedzi.", Array.Empty<int>());
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                var parsed = ParseErrorList(errors, "code", "description");
                if (parsed.Messages.Count > 0)
                    return new ParsedProblem(LimitProblemText(string.Join(" | ", parsed.Messages)), parsed.Codes);
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("exception", out var exception) &&
                exception.ValueKind == JsonValueKind.Object &&
                exception.TryGetProperty("exceptionDetailList", out var legacyErrors) &&
                legacyErrors.ValueKind == JsonValueKind.Array)
            {
                var parsed = ParseErrorList(legacyErrors, "exceptionCode", "exceptionDescription");
                if (parsed.Messages.Count > 0)
                    return new ParsedProblem(LimitProblemText(string.Join(" | ", parsed.Messages)), parsed.Codes);
            }

            foreach (var name in new[] { "detail", "title", "message" })
            {
                var text = GetString(root, name);
                if (!string.IsNullOrWhiteSpace(text))
                    return new ParsedProblem(LimitProblemText(text), Array.Empty<int>());
            }
        }
        catch (JsonException)
        {
            // Odpowiedź nie była JSON-em. Zwracamy bezpiecznie skrócony tekst.
        }
        return new ParsedProblem(LimitProblemText(body), Array.Empty<int>());
    }

    private static ParsedErrorList ParseErrorList(JsonElement errors, string codeProperty, string descriptionProperty)
    {
        var messages = new List<string>();
        var codes = new List<int>();
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object) continue;
            var hasCode = TryGetErrorCode(error, codeProperty, out var code);
            if (hasCode && !codes.Contains(code)) codes.Add(code);

            var description = GetString(error, descriptionProperty).Trim();
            var details = new List<string>();
            if (error.TryGetProperty("details", out var detailList) && detailList.ValueKind == JsonValueKind.Array)
                foreach (var detail in detailList.EnumerateArray())
                {
                    var text = detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) details.Add(text.Trim());
                }

            var parts = new List<string>();
            if (hasCode) parts.Add($"błąd {code}");
            if (!string.IsNullOrWhiteSpace(description)) parts.Add(description.TrimEnd('.'));
            parts.AddRange(details.Select(x => x.TrimEnd('.')));
            if (parts.Count > 0) messages.Add(string.Join(": ", parts) + ".");
        }
        return new ParsedErrorList(messages, codes);
    }

    private static bool TryGetErrorCode(JsonElement error, string propertyName, out int code)
    {
        code = default;
        if (!error.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out code);
        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    private static string LimitProblemText(string text)
    {
        const int maximumLength = 1_500;
        var normalized = text.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "…";
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new KsefApiException("KSeF zwrócił odpowiedź w niepoprawnym formacie JSON.", exception);
        }
    }

    private static InvoiceMetadata ParseMetadata(JsonElement element)
    {
        var seller = element.TryGetProperty("seller", out var sellerElement) ? sellerElement : default;
        var buyer = element.TryGetProperty("buyer", out var buyerElement) ? buyerElement : default;
        var buyerIdentifier = buyer.ValueKind == JsonValueKind.Object && buyer.TryGetProperty("identifier", out var identifier)
            ? GetString(identifier, "value")
            : string.Empty;
        var formCode = element.TryGetProperty("formCode", out var form) ? GetString(form, "systemCode") : string.Empty;

        return new InvoiceMetadata(
            GetString(element, "ksefNumber"),
            GetString(element, "invoiceNumber"),
            GetDateOnly(element, "issueDate"),
            GetDateTimeOffset(element, "invoicingDate"),
            GetDateTimeOffset(element, "acquisitionDate"),
            GetDateTimeOffset(element, "permanentStorageDate"),
            GetString(seller, "name"),
            GetString(seller, "nip"),
            GetString(buyer, "name"),
            buyerIdentifier,
            GetDecimal(element, "netAmount"),
            GetDecimal(element, "vatAmount"),
            GetDecimal(element, "grossAmount"),
            GetString(element, "currency"),
            GetString(element, "invoiceType"),
            formCode,
            GetString(element, "invoicingMode"),
            GetBoolean(element, "hasAttachment"),
            GetBoolean(element, "isSelfInvoicing"));
    }

    private static TokenInfo ParseTokenInfo(JsonElement element) => new(
        GetRequiredString(element, "token"),
        GetDateTimeOffset(element, "validUntil") ?? DateTimeOffset.UtcNow.AddMinutes(5));

    private static string GetRequiredString(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new KsefApiException($"Odpowiedź KSeF nie zawiera pola {name}.");
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
    }

    private static bool GetBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean();

    private static decimal GetDecimal(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)) return 0m;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var result)) return result;
        return decimal.TryParse(property.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? result : 0m;
    }

    private static DateOnly GetDateOnly(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : DateOnly.MinValue;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name) =>
        TryGetDateTimeOffset(element, name, out var value) ? value : null;

    private static bool TryGetDateTimeOffset(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        var text = GetString(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    public void Dispose()
    {
        _accessToken = null;
        _refreshToken = null;
        _ksefToken = string.Empty;
        _metadataPacingGate.Dispose();
        _authGate.Dispose();
        _http.Dispose();
        _handler.Dispose();
    }

    private sealed record TokenInfo(string Token, DateTimeOffset ValidUntil);
    private sealed record EncryptionCertificate(byte[] CertificateDer, string PublicKeyId);
    private sealed record ParsedProblem(string Message, IReadOnlyList<int> ErrorCodes);
    private sealed record ParsedErrorList(IReadOnlyList<string> Messages, IReadOnlyList<int> Codes);
}

internal sealed class KsefApiException : Exception
{
    public KsefApiException()
    {
    }

    public KsefApiException(string message)
        : base(message)
    {
    }

    public KsefApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public KsefApiException(string message, HttpStatusCode? statusCode)
        : base(message) => StatusCode = statusCode;

    public KsefApiException(
        string message,
        HttpStatusCode? statusCode,
        Exception? innerException = null,
        TimeSpan? retryAfter = null,
        IReadOnlyList<int>? errorCodes = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ErrorCodes = errorCodes ?? Array.Empty<int>();
    }

    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public IReadOnlyList<int> ErrorCodes { get; } = Array.Empty<int>();
    public bool HasErrorCode(int code) => ErrorCodes.Contains(code);
}
