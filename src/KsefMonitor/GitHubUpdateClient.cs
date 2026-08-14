using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed record GitHubReleaseFetchResult(
    GitHubReleaseInfo? Release,
    string? ETag,
    bool NotModified);

internal sealed record UpdateDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(DownloadedBytes * 100L / TotalBytes, 0, 100);
}

internal sealed class GitHubUpdateClient : IDisposable
{
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumRateLimitDelay = TimeSpan.FromDays(1);
    private readonly HttpClient _http;

    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxResponseHeadersLength = 64
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = GitHubReleasePolicy.MaximumMetadataBytes
        };
    }

    public async Task<GitHubReleaseFetchResult> GetLatestReleaseAsync(
        string? etag,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(ProductInformation.LatestReleaseApiUri, "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ProductInformation.GitHubApiVersion);
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var entityTag))
            request.Headers.IfNoneMatch.Add(entityTag);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new GitHubReleaseFetchResult(null, etag, NotModified: true);
        EnsureSuccess(response, "pobrać informacji o najnowszym wydaniu");

        var bytes = await ReadLimitedAsync(
            response.Content,
            GitHubReleasePolicy.MaximumMetadataBytes,
            expectedSize: null,
            cancellationToken).ConfigureAwait(false);
        var release = GitHubReleasePolicy.ParseRelease(bytes);
        return new GitHubReleaseFetchResult(
            release,
            response.Headers.ETag?.ToString(),
            NotModified: false);
    }

    public async Task<byte[]> DownloadSmallAssetAsync(
        GitHubReleaseInfo release,
        GitHubReleaseAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        GitHubReleasePolicy.ValidateInitialAssetUri(asset.DownloadUri, release.Tag, asset.Name);
        using var response = await SendAssetRequestAsync(asset.DownloadUri, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadLimitedAsync(
            response.Content,
            maximumBytes,
            asset.Size,
            cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!GitHubReleasePolicy.HashesEqual(actualHash, asset.Sha256Digest))
            throw IntegrityFailure($"Skrót SHA-256 pliku {asset.Name} nie zgadza się z metadanymi GitHub.");
        return bytes;
    }

    public async Task<string> DownloadExecutableAsync(
        GitHubReleaseInfo release,
        string destinationPath,
        string expectedHash,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var asset = release.Executable;
        GitHubReleasePolicy.ValidateInitialAssetUri(asset.DownloadUri, release.Tag, asset.Name);
        if (!GitHubReleasePolicy.HashesEqual(asset.Sha256Digest, expectedHash))
            throw IntegrityFailure("Suma z pliku kontrolnego nie zgadza się z metadanymi EXE na GitHubie.");

        try
        {
            using var response = await SendAssetRequestAsync(asset.DownloadUri, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
                throw IntegrityFailure($"GitHub zapowiedział {contentLength} bajtów zamiast {asset.Size}.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rented = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > asset.Size || total > GitHubReleasePolicy.MaximumExecutableBytes)
                        throw IntegrityFailure("Pobrany plik EXE jest większy niż wynika z metadanych wydania.");
                    hash.AppendData(rented, 0, read);
                    await destination.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(total, asset.Size));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }

            if (total != asset.Size)
                throw IntegrityFailure($"Pobrano {total} bajtów zamiast oczekiwanych {asset.Size}.");
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!GitHubReleasePolicy.HashesEqual(actualHash, expectedHash) ||
                !GitHubReleasePolicy.HashesEqual(actualHash, asset.Sha256Digest))
                throw IntegrityFailure("Skrót pobranego EXE nie zgadza się z wydaniem GitHub.");
            progress?.Report(new UpdateDownloadProgress(total, asset.Size));
            return actualHash;
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAssetRequestAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= GitHubReleasePolicy.MaximumRedirects; redirect++)
        {
            using var request = CreateRequest(current, "application/octet-stream");
            var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                try
                {
                    EnsureSuccess(response, "pobrać pliku aktualizacji");
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new AppUpdateException(
                    "GitHub zwrócił przekierowanie bez nagłówka Location.",
                    "Nie udało się bezpiecznie pobrać aktualizacji z GitHuba.");
            if (redirect == GitHubReleasePolicy.MaximumRedirects)
                throw new AppUpdateException(
                    "GitHub zwrócił zbyt wiele przekierowań.",
                    "Nie udało się bezpiecznie pobrać aktualizacji z GitHuba.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            GitHubReleasePolicy.ValidateRedirectUri(current);
        }

        throw new AppUpdateException(
            "Przekroczono limit przekierowań pobierania.",
            "Nie udało się bezpiecznie pobrać aktualizacji z GitHuba.");
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(ProductInformation.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        long maximumBytes,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength &&
            (contentLength > maximumBytes || expectedSize is { } expected && contentLength != expected))
            throw IntegrityFailure("Rozmiar odpowiedzi nie zgadza się z metadanymi wydania.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(expectedSize is > 0 and <= int.MaxValue ? (int)expectedSize.Value : 0);
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maximumBytes || expectedSize is { } expectedTotal && total > expectedTotal)
                    throw IntegrityFailure("Odpowiedź jest większa niż pozwalają metadane wydania.");
                destination.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        if (expectedSize is { } exact && total != exact)
            throw IntegrityFailure("Odpowiedź jest krótsza niż wynika z metadanych wydania.");
        return destination.ToArray();
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var retryAfter = GetRetryAfter(response) ?? DefaultRateLimitDelay;
            throw new AppUpdateException(
                $"GitHub ograniczył żądanie HTTP {status}. Ponowienie najwcześniej za {retryAfter}.",
                "GitHub chwilowo ograniczył sprawdzanie aktualizacji. Aplikacja poczeka do wskazanego terminu.",
                retryAfter);
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new AppUpdateException(
                "GitHub nie znalazł publicznego wydania lub pliku aktualizacji (HTTP 404).",
                "Nie znaleziono kompletnego publicznego wydania aplikacji na GitHubie.");
        var serverRetryAfter = GetRetryAfter(response);
        if (status >= 500 && serverRetryAfter is { } serviceRetryAfter && serviceRetryAfter > TimeSpan.Zero)
            throw new AppUpdateException(
                $"GitHub zwrócił HTTP {status} i poprosił o przerwę {serviceRetryAfter}.",
                "GitHub jest chwilowo niedostępny. Aplikacja poczeka do wskazanego terminu.",
                serviceRetryAfter);
        throw new AppUpdateException(
            $"Nie udało się {operation}: HTTP {status} {response.ReasonPhrase}.",
            "Nie udało się połączyć z usługą aktualizacji GitHub. Spróbuj ponownie później.");
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (delay is null && response.Headers.RetryAfter?.Date is { } date)
            delay = date - DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            var resetValue = values.FirstOrDefault();
            if (long.TryParse(resetValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                try
                {
                    var resetDelay = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
                    if (delay is null || resetDelay > delay) delay = resetDelay;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Niepoprawny nagłówek nie może zablokować aktualizatora na zawsze.
                }
            }
        }

        if (delay is null) return null;
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaximumRateLimitDelay ? MaximumRateLimitDelay : delay;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static AppUpdateException IntegrityFailure(string technicalMessage) => new(
        technicalMessage,
        "Nie udało się potwierdzić integralności pobieranej aktualizacji. Instalacja została zatrzymana.");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Nie maskuj pierwotnego błędu pobierania nieudaną próbą sprzątania pliku tymczasowego.
        }
    }
}
