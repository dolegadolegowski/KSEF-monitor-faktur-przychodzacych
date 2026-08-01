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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly string _nip;
    private string _ksefToken;
    private TokenInfo? _accessToken;
    private TokenInfo? _refreshToken;

    public KsefApiClient(AppSettings settings, string ksefToken, HttpMessageHandler? handler = null)
    {
        _nip = NipValidator.Normalize(settings.Nip);
        _ksefToken = ksefToken.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = settings.GetBaseUri();
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KSeFMonitor/0.1 (Windows 11)");
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
        var pageOffset = 0;
        var rangeFrom = from;
        var guard = 0;

        while (guard++ < 200)
        {
            var body = new
            {
                subjectType = "Subject2",
                dateRange = new
                {
                    dateType = "PermanentStorage",
                    from = rangeFrom.ToString("O", CultureInfo.InvariantCulture),
                    restrictToPermanentStorageHwmDate = true
                }
            };

            using var response = await SendAuthorizedAsync(
                HttpMethod.Post,
                $"invoices/query/metadata?sortOrder=Asc&pageOffset={pageOffset}&pageSize=250",
                body,
                cancellationToken).ConfigureAwait(false);

            using var document = await ParseJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (TryGetDateTimeOffset(root, "permanentStorageHwmDate", out var responseHwm)) hwm = responseHwm;

            var page = new List<InvoiceMetadata>();
            if (root.TryGetProperty("invoices", out var invoices) && invoices.ValueKind == JsonValueKind.Array)
            {
                foreach (var invoice in invoices.EnumerateArray())
                {
                    var parsed = ParseMetadata(invoice);
                    if (string.IsNullOrWhiteSpace(parsed.KsefNumber)) continue;
                    collected[parsed.KsefNumber] = parsed;
                    page.Add(parsed);
                }
            }

            var hasMore = GetBoolean(root, "hasMore");
            var truncated = GetBoolean(root, "isTruncated");
            if (!hasMore) break;

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

        if (guard >= 200) throw new KsefApiException("Przekroczono bezpieczny limit stron odpowiedzi KSeF.");
        return new MetadataQueryResult(collected.Values.OrderBy(x => x.PermanentStorageDate).ToList(), hwm);
    }

    public async Task<string> DownloadInvoiceXmlAsync(string ksefNumber, CancellationToken cancellationToken)
    {
        var escaped = Uri.EscapeDataString(ksefNumber);
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"invoices/ksef/{escaped}", null, cancellationToken)
            .ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
                catch (KsefApiException)
                {
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
        CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(method, relativeUri, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken!.Token);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _accessToken = null;
            await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = TryGetProblemMessage(body);
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is { } retryAfter)
            message += $" Spróbuj ponownie po: {retryAfter}.";
        response.Dispose();
        throw new KsefApiException($"KSeF HTTP {(int)response.StatusCode}: {message}", response.StatusCode);
    }

    private static string TryGetProblemMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Brak treści odpowiedzi.";
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            foreach (var name in new[] { "detail", "title", "message" })
            {
                var text = GetString(root, name);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            if (root.TryGetProperty("exception", out var exception))
            {
                var description = GetString(exception, "exceptionDescription");
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
        }
        catch (JsonException)
        {
            // Odpowiedź nie była JSON-em. Zwracamy bezpiecznie skrócony tekst.
        }
        return body.Length <= 500 ? body : body[..500];
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        _authGate.Dispose();
        _http.Dispose();
    }

    private sealed record TokenInfo(string Token, DateTimeOffset ValidUntil);
    private sealed record EncryptionCertificate(byte[] CertificateDer, string PublicKeyId);
}

internal sealed class KsefApiException : Exception
{
    public KsefApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}
