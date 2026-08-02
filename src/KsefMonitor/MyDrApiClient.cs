using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class MyDrApiClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumPages = 1_000;
    private static readonly Uri ProductionBaseUri = new("https://edm.mydr.pl/secure/ext_api/");
    private static readonly HashSet<string> SafeOAuthErrorCodes = new(StringComparer.Ordinal)
    {
        "access_denied",
        "invalid_client",
        "invalid_grant",
        "invalid_request",
        "invalid_scope",
        "server_error",
        "temporarily_unavailable",
        "unauthorized_client",
        "unsupported_grant_type"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly HttpClient _http;
    private readonly Action<string>? _rotatedRefreshTokenHandler;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly object _tokenStateLock = new();
    private string _clientId;
    private string _clientSecret;
    private string _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _accessTokenValidUntilUtc;
    private string? _pendingRotatedRefreshToken;
    private bool _disposed;

    public MyDrApiClient(
        MyDrCredentials credentials,
        HttpMessageHandler? handler = null,
        Action<string>? rotatedRefreshTokenHandler = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        credentials.NormalizeAfterLoad();

        _clientId = credentials.ClientId.Trim();
        _clientSecret = credentials.ClientSecret;
        _refreshToken = credentials.RefreshToken;
        _rotatedRefreshTokenHandler = rotatedRefreshTokenHandler;

        var effectiveHandler = handler ?? CreateDefaultHandler();
        _http = new HttpClient(effectiveHandler, disposeHandler: handler is null)
        {
            BaseAddress = ProductionBaseUri,
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KSeFMonitor/0.5.2 (Windows 11)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<MyDrTokenResult> AuthenticateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    public string? TakeRotatedRefreshToken() => Interlocked.Exchange(ref _pendingRotatedRefreshToken, null);

    public async Task<IReadOnlyList<MyDrVisit>> GetPrivateVisitsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (toInclusive < fromInclusive)
            throw new ArgumentOutOfRangeException(nameof(toInclusive), "Data końcowa nie może być wcześniejsza od początkowej.");

        var visits = new Dictionary<long, MyDrVisit>();
        var completed = false;
        int? expectedCount = null;

        for (var pageNumber = 1; pageNumber <= MaximumPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeUri = BuildVisitsUri(fromInclusive, toInclusive, pageNumber);
            using var response = await SendAuthorizedGetAsync(relativeUri, cancellationToken).ConfigureAwait(false);
            var page = await DeserializeAsync<MyDrVisitPage>(
                response,
                "MyDR zwrócił niepoprawną listę wizyt.",
                cancellationToken).ConfigureAwait(false);

            if (page?.Results is null)
                throw new MyDrApiException("MyDR zwrócił niepełną listę wizyt.");
            if (page.Count is { } count)
            {
                if (count < 0 || expectedCount is { } previousCount && previousCount != count)
                    throw new MyDrApiException("MyDR zmienił liczbę wizyt podczas pobierania stron.");
                expectedCount = count;
            }

            foreach (var visit in page.Results)
            {
                if (visit is null || visit.Id <= 0)
                    throw new MyDrApiException("MyDR zwrócił wizytę bez poprawnego identyfikatora.");
                if (!string.IsNullOrWhiteSpace(visit.VisitKind) &&
                    !string.Equals(visit.VisitKind, "Prywatna", StringComparison.OrdinalIgnoreCase))
                    throw new MyDrApiException("MyDR zwrócił wizytę o innym rodzaju niż prywatna.");

                // Weryfikacja daty zapobiega zapisaniu częściowo uszkodzonej migawki.
                _ = visit.GetDate();
                if (!visits.TryAdd(visit.Id, visit))
                    throw new MyDrApiException("MyDR zwrócił tę samą wizytę na więcej niż jednej stronie.");
            }

            if (IsLastPage(page, pageNumber))
            {
                completed = true;
                break;
            }
        }

        if (!completed) throw new MyDrApiException("MyDR zwrócił zbyt wiele stron z listą wizyt.");
        if (expectedCount is { } total && visits.Count != total)
            throw new MyDrApiException("MyDR zwrócił niepełny zestaw wizyt dla wybranego miesiąca.");

        return visits.Values
            .OrderBy(visit => visit.Date, StringComparer.Ordinal)
            .ThenBy(visit => visit.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<MyDrAttachedPrivateService>> GetVisitServicesAsync(
        long visitId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (visitId <= 0) throw new ArgumentOutOfRangeException(nameof(visitId));

        using var response = await SendAuthorizedGetAsync(
            $"visits/{visitId.ToString(CultureInfo.InvariantCulture)}/services/",
            cancellationToken).ConfigureAwait(false);
        var services = await DeserializeServiceListAsync(response, cancellationToken).ConfigureAwait(false);

        if (services is null)
            throw new MyDrApiException("MyDR zwrócił niepełną listę usług wizyty.");
        if (services.Any(service => service is null || service.Id <= 0))
            throw new MyDrApiException("MyDR zwrócił usługę bez poprawnego identyfikatora.");
        if (services.Select(service => service.Id).Distinct().Count() != services.Count)
            throw new MyDrApiException("MyDR zwrócił tę samą usługę więcej niż jeden raz.");

        return services;
    }

    private static async Task<List<MyDrAttachedPrivateService>> DeserializeServiceListAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                var receivedKind = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Object => "obiekt",
                    JsonValueKind.String => "tekst",
                    JsonValueKind.Number => "liczbę",
                    JsonValueKind.True or JsonValueKind.False => "wartość logiczną",
                    JsonValueKind.Null => "null",
                    _ => "inny format"
                };
                throw new MyDrApiException(
                    $"MyDR zwrócił niepoprawną listę usług wizyty: oczekiwano tablicy JSON, otrzymano {receivedKind}.");
            }

            return document.RootElement.Deserialize<List<MyDrAttachedPrivateService>>(JsonOptions)
                   ?? throw new MyDrApiException("MyDR zwrócił niepełną listę usług wizyty.");
        }
        catch (MyDrApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Nie logujemy odpowiedzi ani wyjątku parsera: elementy mogą zawierać
            // nazwy usług lub inne dane medyczne. DTO celowo odczytuje tylko id i value.
            throw new MyDrApiException(
                "MyDR zwrócił niepoprawną listę usług wizyty: typ jednego z wymaganych pól jest niezgodny z dokumentacją.");
        }
    }

    public static decimal GetServiceGrossValue(MyDrAttachedPrivateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.GetGrossValue();
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (HasUsableAccessToken()) return;

        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasUsableAccessToken()) return;
            _ = await AuthenticateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<MyDrTokenResult> AuthenticateCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_clientId) ||
            string.IsNullOrWhiteSpace(_clientSecret) ||
            string.IsNullOrWhiteSpace(_refreshToken))
            throw new MyDrApiException("Nie zapisano kompletnych danych dostępowych MyDR.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "o/token/")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", _refreshToken),
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret)
            })
        };

        using var response = await SendAsync(request, isOAuthRequest: true, cancellationToken).ConfigureAwait(false);
        var token = await DeserializeAsync<MyDrRawToken>(
            response,
            "MyDR zwrócił niepoprawną odpowiedź podczas logowania.",
            cancellationToken).ConfigureAwait(false);

        if (token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            token.ExpiresIn <= 0 ||
            !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new MyDrApiException("MyDR zwrócił niepełne dane logowania.");

        var scopes = token.Scope?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (token.Requires2Fa || !scopes.Contains("external_api", StringComparer.Ordinal))
            throw new MyDrApiException("Dostęp MyDR wymaga potwierdzenia 2FA lub nie ma uprawnienia external_api.");

        string? rotatedRefreshToken = null;
        lock (_tokenStateLock)
        {
            _accessToken = token.AccessToken;
            _accessTokenValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);

            if (!string.IsNullOrWhiteSpace(token.RefreshToken) &&
                !string.Equals(token.RefreshToken, _refreshToken, StringComparison.Ordinal))
            {
                _refreshToken = token.RefreshToken;
                rotatedRefreshToken = token.RefreshToken;
                _pendingRotatedRefreshToken = token.RefreshToken;
            }
        }

        // Zapis musi nastąpić bezpośrednio po każdej rotacji, również gdy nowe
        // logowanie zostało wywołane automatycznie po odpowiedzi HTTP 401.
        if (rotatedRefreshToken is not null) _rotatedRefreshTokenHandler?.Invoke(rotatedRefreshToken);

        return new MyDrTokenResult(rotatedRefreshToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedGetAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var attemptedToken = GetAccessToken();
        var response = await SendAuthorizedGetCoreAsync(relativeUri, attemptedToken, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            await EnsureSuccessAsync(response, isOAuthRequest: false, cancellationToken).ConfigureAwait(false);
            return response;
        }

        response.Dispose();
        InvalidateAccessToken(attemptedToken);
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        response = await SendAuthorizedGetCoreAsync(relativeUri, GetAccessToken(), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, isOAuthRequest: false, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> SendAuthorizedGetCoreAsync(
        string relativeUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await SendAsync(request, isOAuthRequest: false, cancellationToken, ensureSuccess: false).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool isOAuthRequest,
        CancellationToken cancellationToken,
        bool ensureSuccess = true)
    {
        try
        {
            var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (ensureSuccess)
                await EnsureSuccessAsync(response, isOAuthRequest, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MyDrApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new MyDrApiException("Nie udało się połączyć z MyDR. Sprawdź połączenie z internetem.");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        bool isOAuthRequest,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var statusCode = response.StatusCode;
        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
        string? oauthErrorCode = null;
        if (isOAuthRequest)
            oauthErrorCode = await ReadSafeOAuthErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

        response.Dispose();
        var message = isOAuthRequest
            ? CreateAuthenticationErrorMessage(statusCode, oauthErrorCode)
            : $"MyDR zwrócił błąd HTTP {(int)statusCode}.";
        throw new MyDrApiException(message, statusCode, oauthErrorCode, retryAfter);
    }

    private static async Task<string?> ReadSafeOAuthErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.String)
                return null;

            var code = error.GetString();
            return code is not null && SafeOAuthErrorCodes.Contains(code) ? code : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string CreateAuthenticationErrorMessage(HttpStatusCode statusCode, string? oauthErrorCode) =>
        oauthErrorCode switch
        {
            "invalid_client" => "MyDR odrzucił Client ID lub Client Secret.",
            "invalid_grant" => "MyDR odrzucił Refresh Token. Zapisz aktualny token w ustawieniach.",
            "invalid_scope" => "Dane MyDR nie mają wymaganego uprawnienia external_api.",
            _ => $"MyDR odrzucił próbę logowania (HTTP {(int)statusCode})."
        };

    private static async Task<T?> DeserializeAsync<T>(
        HttpResponseMessage response,
        string safeErrorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Nie dołączamy wyjątku parsera: odpowiedź może zawierać dane medyczne
            // lub token, które nie powinny trafić do logu aplikacji.
            throw new MyDrApiException(safeErrorMessage);
        }
    }

    private static string BuildVisitsUri(DateOnly fromInclusive, DateOnly toInclusive, int pageNumber) =>
        "visits/?visit_kind=Prywatna" +
        $"&date_from={fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
        $"&date_to={toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
        $"&ordering=date&page={pageNumber.ToString(CultureInfo.InvariantCulture)}" +
        $"&page_size={PageSize.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsLastPage(MyDrVisitPage page, int requestedPage)
    {
        if (page.CurrentPage is { } currentPage && currentPage != requestedPage)
            throw new MyDrApiException("MyDR zwrócił nieprawidłowy numer strony wizyt.");

        if (page.LastPage is { } lastPage)
        {
            if (lastPage < 1 || lastPage < requestedPage)
                throw new MyDrApiException("MyDR zwrócił nieprawidłową liczbę stron wizyt.");
            return requestedPage >= lastPage;
        }

        // Pole next służy wyłącznie jako informacja, że istnieje kolejna strona.
        // Adresu z odpowiedzi celowo nie używamy, więc host pozostaje produkcyjny.
        return string.IsNullOrWhiteSpace(page.Next);
    }

    private bool HasUsableAccessToken()
    {
        lock (_tokenStateLock)
            return !string.IsNullOrWhiteSpace(_accessToken) &&
                   _accessTokenValidUntilUtc > DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private string GetAccessToken()
    {
        lock (_tokenStateLock)
            return _accessToken ?? throw new MyDrApiException("Nie udało się uzyskać dostępu do MyDR.");
    }

    private void InvalidateAccessToken(string attemptedToken)
    {
        lock (_tokenStateLock)
        {
            if (!string.Equals(_accessToken, attemptedToken, StringComparison.Ordinal)) return;
            _accessToken = null;
            _accessTokenValidUntilUtc = default;
        }
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value?.Date is not { } date) return null;
        var delay = date - DateTimeOffset.UtcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        // OAuth wysyła Client Secret i Refresh Token w treści POST. Nie wolno
        // automatycznie powtarzać tego żądania pod adresem z nagłówka Location.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_tokenStateLock)
        {
            _clientId = string.Empty;
            _clientSecret = string.Empty;
            _refreshToken = string.Empty;
            _accessToken = null;
            _pendingRotatedRefreshToken = null;
            _accessTokenValidUntilUtc = default;
        }
        _authGate.Dispose();
        _http.Dispose();
    }
}

internal sealed class MyDrApiException : Exception
{
    public MyDrApiException(string message)
        : base(message)
    {
    }

    public MyDrApiException(
        string message,
        HttpStatusCode? statusCode,
        string? oauthErrorCode = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        OAuthErrorCode = oauthErrorCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? OAuthErrorCode { get; }
    public TimeSpan? RetryAfter { get; }
}
