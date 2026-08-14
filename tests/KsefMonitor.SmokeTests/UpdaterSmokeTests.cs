using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal static class UpdaterSmokeTests
{
    private static readonly string[] InvalidPostUpdateArguments = { "--post-update", "update.json", "../nonce" };
    private static readonly string[] IncompleteHelperArguments = { "--update-helper", "update.json" };

    public static async Task RunAsync(Action<bool, string> require)
    {
        TestSemanticVersions(require);
        TestReleasePolicy(require);
        TestChecksumParser(require);
        await TestGitHubClientAsync(require);
        await TestDownloadIntegrityAsync(require);
        await TestUpdateServiceAsync(require);
        TestFileTransaction(require);
        TestInvocationParsing(require);
    }

    public static async Task RunLiveGitHubCheckAsync(string expectedTag, Action<bool, string> require)
    {
        require(SemanticVersion.TryParseReleaseTag(expectedTag, out _),
            "Oczekiwany tag live check ma niepoprawny format.");
        using var client = new GitHubUpdateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var result = await client.GetLatestReleaseAsync(null, timeout.Token);
        var release = result.Release;
        if (release is null)
        {
            require(false, "GitHub latest nie zwrócił wydania.");
            return;
        }
        require(string.Equals(release.Tag, expectedTag, StringComparison.Ordinal),
            $"GitHub latest wskazuje {release.Tag}, oczekiwano {expectedTag}.");
        var checksumBytes = await client.DownloadSmallAssetAsync(
            release,
            release.Checksum,
            GitHubReleasePolicy.MaximumChecksumBytes,
            timeout.Token);
        var checksumHash = ReleaseChecksumParser.Parse(checksumBytes);
        require(GitHubReleasePolicy.HashesEqual(checksumHash, release.Executable.Sha256Digest),
            "Rzeczywisty plik checksum nie zgadza się z digestem EXE na GitHubie.");
    }

    public static void CheckPublishedExecutable(string path, string expectedVersion, Action<bool, string> require)
    {
        var fullPath = Path.GetFullPath(path);
        require(File.Exists(fullPath), "Nie znaleziono opublikowanego KSeFMonitor.exe.");
        require(string.Equals(Path.GetFileName(fullPath), ProductInformation.WindowsReleaseAssetName, StringComparison.Ordinal),
            "Opublikowany EXE ma inną nazwę niż oczekuje aktualizator.");
        using (var stream = File.OpenRead(fullPath))
            require(stream.ReadByte() == 'M' && stream.ReadByte() == 'Z', "Opublikowany plik nie ma nagłówka PE/MZ.");
        require(SemanticVersion.TryParseReleaseTag($"v{expectedVersion}", out var expected), "Test otrzymał niepoprawną oczekiwaną wersję.");

        if (OperatingSystem.IsWindows())
        {
            var rawVersion = FileVersionInfo.GetVersionInfo(fullPath).FileVersion;
            require(Version.TryParse(rawVersion, out var fileVersion), $"Nie udało się odczytać FileVersion z EXE: {rawVersion ?? "(brak)"}.");
            require(SemanticVersion.FromAssemblyVersion(fileVersion) == expected,
                $"FileVersion {rawVersion} nie odpowiada wydaniu v{expectedVersion}.");
        }
        else
        {
            require(FileContainsAscii(fullPath, $"{expectedVersion}.0"),
                $"Opublikowany EXE nie zawiera oczekiwanej wersji pliku {expectedVersion}.0.");
        }

        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!
        };
        start.ArgumentList.Add("--update-helper");
        start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(fullPath)!, "missing-update.json"));
        start.ArgumentList.Add(new string('a', 32));
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Nie udało się uruchomić trybu helpera z opublikowanego EXE.");
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            require(false, "Tryb helpera nie zakończył się w limicie 60 sekund; mógł uruchomić interfejs WPF.");
        }
        require(process.ExitCode == 30,
            $"Tryb helpera zwrócił kod {process.ExitCode} zamiast kontrolowanego kodu 30 dla brakującego deskryptora.");
    }

    private static bool FileContainsAscii(string path, string value)
    {
        var needle = Encoding.ASCII.GetBytes(value);
        var buffer = new byte[64 * 1024 + needle.Length - 1];
        var carry = 0;
        using var stream = File.OpenRead(path);
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0) return false;
            var length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, length);
            buffer.AsSpan(length - carry, carry).CopyTo(buffer);
        }
    }

    private static void TestSemanticVersions(Action<bool, string> require)
    {
        require(SemanticVersion.TryParseReleaseTag("v0.6.0", out var version) && version == new SemanticVersion(0, 6, 0),
            "Updater nie odczytuje poprawnego tagu SemVer.");
        require(new SemanticVersion(1, 0, 0).CompareTo(new SemanticVersion(0, 99, 99)) > 0,
            "Updater porównuje wersje tekstowo zamiast numerycznie.");
        require(new SemanticVersion(0, 10, 0).CompareTo(new SemanticVersion(0, 9, 99)) > 0,
            "Updater niepoprawnie porównuje numer minor.");
        foreach (var invalid in new[] { "0.6.0", "v0.6", "v0.6.0.1", "v01.6.0", "v0.06.0", "v0.6.00", "v0.6.0-rc.1", " v0.6.0", "v0.6.0 " })
            require(!SemanticVersion.TryParseReleaseTag(invalid, out _), $"Updater zaakceptował niedozwolony tag: {invalid}.");
    }

    private static void TestReleasePolicy(Action<bool, string> require)
    {
        var executable = Encoding.ASCII.GetBytes("MZ-test-executable");
        var checksum = Encoding.ASCII.GetBytes($"{Sha(executable)}  {ProductInformation.WindowsReleaseAssetName}\r\n");
        var json = CreateReleaseJson("v0.6.0", executable, checksum);
        var release = GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json));
        require(release.Version == new SemanticVersion(0, 6, 0), "Parser wydania zgubił wersję.");
        require(release.Executable.Size == executable.Length && release.Checksum.Size == checksum.Length,
            "Parser wydania zgubił rozmiary plików.");

        RequireUpdateFailure(require, () => GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json.Replace("\"draft\":false", "\"draft\":true", StringComparison.Ordinal))),
            "Updater zaakceptował draft GitHub Release.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json.Replace("\"immutable\":true", "\"immutable\":false", StringComparison.Ordinal))),
            "Updater zaakceptował modyfikowalne GitHub Release.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json.Replace("https://github.com/", "http://github.com/", StringComparison.Ordinal))),
            "Updater zaakceptował pobieranie po HTTP.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json.Replace("github.com/dolegadolegowski", "github.com.evil/dolegadolegowski", StringComparison.Ordinal))),
            "Updater zaakceptował host podszywający się pod GitHub.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(json.Replace("\"digest\":\"sha256:", "\"digest\":\"md5:", StringComparison.Ordinal))),
            "Updater zaakceptował inny algorytm niż SHA-256.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ValidateRedirectUri(new Uri("https://release-assets.githubusercontent.com.evil/file")),
            "Updater zaakceptował fałszywy host przekierowania.");
        RequireUpdateFailure(require, () => GitHubReleasePolicy.ValidateRedirectUri(new Uri("http://release-assets.githubusercontent.com/file")),
            "Updater zaakceptował obniżenie HTTPS do HTTP.");
    }

    private static void TestChecksumParser(Action<bool, string> require)
    {
        var hash = new string('A', 64);
        var valid = Encoding.ASCII.GetBytes($"{hash}  {ProductInformation.WindowsReleaseAssetName}\r\n");
        require(string.Equals(ReleaseChecksumParser.Parse(valid), hash.ToLowerInvariant(), StringComparison.Ordinal),
            "Parser sumy SHA-256 nie normalizuje poprawnego pliku.");
        foreach (var invalid in new[]
                 {
                     $"{hash} {ProductInformation.WindowsReleaseAssetName}\n",
                     $"{hash}  ../{ProductInformation.WindowsReleaseAssetName}\n",
                     $"{hash}  inny.exe\n",
                     $"{hash}  {ProductInformation.WindowsReleaseAssetName}\nDODATKOWY WIERSZ\n",
                     $"{new string('g', 64)}  {ProductInformation.WindowsReleaseAssetName}\n"
                 })
            RequireUpdateFailure(require, () => ReleaseChecksumParser.Parse(Encoding.ASCII.GetBytes(invalid)),
                "Parser zaakceptował niepoprawny plik .sha256.");
    }

    private static async Task TestGitHubClientAsync(Action<bool, string> require)
    {
        var executable = Encoding.ASCII.GetBytes("MZ-client-test");
        var checksum = Encoding.ASCII.GetBytes($"{Sha(executable)}  {ProductInformation.WindowsReleaseAssetName}\n");
        var requestCount = 0;
        using var handler = new UpdateTestHandler((request, _) =>
        {
            requestCount++;
            require(request.RequestUri == ProductInformation.LatestReleaseApiUri, "Klient odpytuje inny endpoint niż oficjalne GitHub latest.");
            require(request.Headers.UserAgent.ToString().StartsWith("KSeFMonitor/", StringComparison.Ordinal), "Brakuje User-Agent GitHub API.");
            require(request.Headers.Contains("X-GitHub-Api-Version"), "Brakuje wersji GitHub REST API.");
            if (requestCount == 2)
            {
                require(request.Headers.IfNoneMatch.Count == 1, "Klient nie wysłał ETag przy kolejnym sprawdzeniu.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var response = Json(CreateReleaseJson("v0.6.0", executable, checksum));
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-etag\"");
            return Task.FromResult(response);
        });
        using var client = new GitHubUpdateClient(handler);
        var first = await client.GetLatestReleaseAsync(null, CancellationToken.None);
        require(first.Release?.Version == new SemanticVersion(0, 6, 0) && first.ETag == "\"release-etag\"",
            "Klient nie odczytał wydania lub ETag.");
        var second = await client.GetLatestReleaseAsync(first.ETag, CancellationToken.None);
        require(second.NotModified && second.Release is null, "Klient nie obsłużył odpowiedzi HTTP 304.");
    }

    private static async Task TestDownloadIntegrityAsync(Action<bool, string> require)
    {
        var executable = Encoding.ASCII.GetBytes("MZ-streamed-update-test");
        var executableHash = Sha(executable);
        var checksum = Encoding.ASCII.GetBytes($"{executableHash}  {ProductInformation.WindowsReleaseAssetName}\r\n");
        var release = GitHubReleasePolicy.ParseRelease(Encoding.UTF8.GetBytes(CreateReleaseJson("v0.6.0", executable, checksum)));
        var cdnBase = new Uri("https://release-assets.githubusercontent.com/test/");
        using var handler = new UpdateTestHandler((request, _) =>
        {
            if (string.Equals(request.RequestUri?.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(cdnBase, request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? "checksum" : "executable");
                return Task.FromResult(response);
            }
            if (request.RequestUri == new Uri(cdnBase, "checksum"))
                return Task.FromResult(Bytes(checksum));
            if (request.RequestUri == new Uri(cdnBase, "executable"))
                return Task.FromResult(Bytes(executable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new GitHubUpdateClient(handler);
        var checksumBytes = await client.DownloadSmallAssetAsync(release, release.Checksum, 4096, CancellationToken.None);
        require(ReleaseChecksumParser.Parse(checksumBytes) == executableHash, "Pobrana suma kontrolna ma inny hash.");

        var directory = Path.Combine(Path.GetTempPath(), $"ksef-updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "candidate.exe");
            var progress = new List<int>();
            var actual = await client.DownloadExecutableAsync(
                release,
                destination,
                executableHash,
                new TestProgress(value => progress.Add(value.Percent)),
                CancellationToken.None);
            require(actual == executableHash && File.ReadAllBytes(destination).SequenceEqual(executable),
                "Strumieniowe pobieranie zmieniło plik EXE.");
            require(progress.Count > 0 && progress[^1] == 100, "Pobieranie nie zgłosiło 100% postępu.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        using var evilHandler = new UpdateTestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/payload.exe");
            return Task.FromResult(response);
        });
        using var evilClient = new GitHubUpdateClient(evilHandler);
        try
        {
            _ = await evilClient.DownloadSmallAssetAsync(release, release.Checksum, 4096, CancellationToken.None);
            require(false, "Klient pobierania zaakceptował przekierowanie poza GitHub.");
        }
        catch (AppUpdateException)
        {
            // Oczekiwane zatrzymanie pobierania.
        }
    }

    private static async Task TestUpdateServiceAsync(Action<bool, string> require)
    {
        var executable = Encoding.ASCII.GetBytes("MZ-service-test");
        var checksum = Encoding.ASCII.GetBytes($"{Sha(executable)}  {ProductInformation.WindowsReleaseAssetName}\n");
        var requestCount = 0;
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new UpdateTestHandler(async (request, cancellationToken) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                firstRequestStarted.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(cancellationToken);
                var response = Json(CreateReleaseJson("v0.6.1", executable, checksum));
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"service-etag\"");
                return response;
            }

            require(request.Headers.IfNoneMatch.Count == 1,
                "AppUpdateService nie użył ETag przy wymuszonym ponownym sprawdzeniu.");
            return requestCount == 2
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var directory = Path.Combine(Path.GetTempPath(), $"ksef-update-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var log = new ApplicationLog(Path.Combine(directory, "app.log"));
            using var client = new GitHubUpdateClient(handler);
            using var service = new AppUpdateService(
                log,
                client,
                processPath: Path.Combine(directory, ProductInformation.WindowsReleaseAssetName),
                currentVersion: new SemanticVersion(0, 6, 0),
                forcedCheckMinimumInterval: TimeSpan.Zero);
            service.StateChanged += (_, _) => throw new InvalidOperationException("Kontrolowany błąd odbiorcy testowego.");

            var first = service.CheckForUpdatesAsync(force: true);
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var coalesced = service.CheckForUpdatesAsync(force: true);
            releaseFirstRequest.TrySetResult();
            var firstSnapshot = await first;
            var coalescedSnapshot = await coalesced;
            require(requestCount == 1 && firstSnapshot.Phase == AppUpdatePhase.Available && coalescedSnapshot.HasAvailableUpdate,
                "AppUpdateService nie scala równoległych sprawdzeń lub nie wykrywa nowszego SemVer.");
            require(firstSnapshot.CurrentVersion == new SemanticVersion(0, 6, 0) &&
                    firstSnapshot.AvailableRelease?.Version == new SemanticVersion(0, 6, 1),
                "AppUpdateService nie używa wstrzykniętej wersji bieżącej.");

            var throttled = await service.CheckForUpdatesAsync(force: false);
            require(requestCount == 1 && throttled.Phase == AppUpdatePhase.Available,
                "AppUpdateService nie ogranicza automatycznych sprawdzeń wykonywanych zbyt często.");

            var notModified = await service.CheckForUpdatesAsync(force: true);
            require(requestCount == 2 && notModified.Phase == AppUpdatePhase.Available && notModified.HasAvailableUpdate,
                "AppUpdateService zgubił dostępną wersję po odpowiedzi HTTP 304.");

            var failedRefresh = await service.CheckForUpdatesAsync(force: true);
            require(requestCount == 3 && failedRefresh.Phase == AppUpdatePhase.Available &&
                    failedRefresh.HasAvailableUpdate && failedRefresh.HasError,
                "AppUpdateService nie zachował wersji lub nie oznaczył błędu ostatniego ponownego sprawdzenia.");

            var cooldownRequests = 0;
            using var cooldownHandler = new UpdateTestHandler((_, _) =>
            {
                cooldownRequests++;
                return Task.FromResult(Json(CreateReleaseJson("v0.6.1", executable, checksum)));
            });
            using var cooldownClient = new GitHubUpdateClient(cooldownHandler);
            using var cooldownService = new AppUpdateService(
                log,
                cooldownClient,
                currentVersion: new SemanticVersion(0, 6, 0),
                forcedCheckMinimumInterval: TimeSpan.FromMinutes(1));
            await cooldownService.CheckForUpdatesAsync(force: true);
            await cooldownService.CheckForUpdatesAsync(force: true);
            require(cooldownRequests == 1,
                "Ręczne sprawdzanie aktualizacji może wykonywać seryjne żądania do GitHub API.");

            foreach (var statusCode in new[]
                     {
                         HttpStatusCode.TooManyRequests,
                         HttpStatusCode.Forbidden,
                         HttpStatusCode.ServiceUnavailable
                     })
            {
                var rateLimitRequests = 0;
                using var rateLimitHandler = new UpdateTestHandler((_, _) =>
                {
                    rateLimitRequests++;
                    var response = new HttpResponseMessage(statusCode);
                    if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                    else
                        response.Headers.TryAddWithoutValidation(
                            "X-RateLimit-Reset",
                            DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                    return Task.FromResult(response);
                });
                using var rateLimitClient = new GitHubUpdateClient(rateLimitHandler);
                using var rateLimitService = new AppUpdateService(
                    log,
                    rateLimitClient,
                    currentVersion: new SemanticVersion(0, 6, 0),
                    forcedCheckMinimumInterval: TimeSpan.Zero);
                var limited = await rateLimitService.CheckForUpdatesAsync(force: true);
                var stillLimited = await rateLimitService.CheckForUpdatesAsync(force: true);
                require(rateLimitRequests == 1 && limited.HasError && stillLimited.HasError,
                    $"AppUpdateService nie respektuje blokady GitHub po HTTP {(int)statusCode}.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var (tag, expectedPhase) in new[]
                 {
                     ("v0.6.0", AppUpdatePhase.UpToDate),
                     ("v0.5.9", AppUpdatePhase.UpToDate)
                 })
        {
            using var handlerForVersion = new UpdateTestHandler((_, _) =>
                Task.FromResult(Json(CreateReleaseJson(tag, executable, checksum))));
            var versionDirectory = Path.Combine(Path.GetTempPath(), $"ksef-update-version-{Guid.NewGuid():N}");
            Directory.CreateDirectory(versionDirectory);
            try
            {
                var log = new ApplicationLog(Path.Combine(versionDirectory, "app.log"));
                using var client = new GitHubUpdateClient(handlerForVersion);
                using var service = new AppUpdateService(log, client, currentVersion: new SemanticVersion(0, 6, 0));
                var snapshot = await service.CheckForUpdatesAsync(force: true);
                require(snapshot.Phase == expectedPhase && !snapshot.HasAvailableUpdate,
                    $"AppUpdateService nie zablokował wersji {tag} jako aktualizacji.");
            }
            finally
            {
                Directory.Delete(versionDirectory, recursive: true);
            }
        }
    }

    private static void TestFileTransaction(Action<bool, string> require)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ksef-update-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "target.exe");
            var replacement = Path.Combine(directory, "replacement.exe");
            var backup = Path.Combine(directory, "backup.exe");
            var failed = Path.Combine(directory, "failed.exe");
            File.WriteAllText(target, "old", Encoding.ASCII);
            File.WriteAllText(replacement, "new", Encoding.ASCII);
            UpdateInstaller.ReplaceFileTransaction(replacement, target, backup);
            require(File.ReadAllText(target, Encoding.ASCII) == "new" && File.ReadAllText(backup, Encoding.ASCII) == "old",
                "Transakcja aktualizacji nie utworzyła kopii starego pliku.");
            UpdateInstaller.RollbackFileTransaction(backup, target, failed);
            require(File.ReadAllText(target, Encoding.ASCII) == "old" && File.ReadAllText(failed, Encoding.ASCII) == "new",
                "Rollback nie przywrócił poprzedniego pliku.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestInvocationParsing(Action<bool, string> require)
    {
        var nonce = new string('a', 32);
        var acceptedMissingDescriptor = UpdateInstaller.TryParsePostUpdateInvocation(
            new[] { "--post-update", "missing-update.json", nonce },
            out var invocation);
        if (OperatingSystem.IsWindows())
        {
            require(!acceptedMissingDescriptor && invocation is null,
                "Updater zaakceptował brakujący deskryptor i pozwolił ominąć blokadę instalatora.");
        }
        else
        {
            require(acceptedMissingDescriptor && invocation is not null,
                "Przenośny test składni nie rozpoznaje poprawnych argumentów startu po aktualizacji.");
        }
        require(!UpdateInstaller.TryParsePostUpdateInvocation(InvalidPostUpdateArguments, out _),
            "Updater zaakceptował niepoprawny nonce.");
        require(!UpdateInstaller.IsHelperInvocation(IncompleteHelperArguments),
            "Updater zaakceptował niepełne argumenty helpera.");
    }

    private static string CreateReleaseJson(string tag, byte[] executable, byte[] checksum)
    {
        var repository = ProductInformation.SourceRepositoryUrl;
        return $$"""
        {
          "tag_name":"{{tag}}",
          "html_url":"{{repository}}/releases/tag/{{tag}}",
          "draft":false,
          "prerelease":false,
          "immutable":true,
          "assets":[
            {
              "name":"{{ProductInformation.WindowsReleaseAssetName}}",
              "state":"uploaded",
              "size":{{executable.Length}},
              "digest":"sha256:{{Sha(executable)}}",
              "browser_download_url":"{{repository}}/releases/download/{{tag}}/{{ProductInformation.WindowsReleaseAssetName}}"
            },
            {
              "name":"{{ProductInformation.WindowsReleaseChecksumAssetName}}",
              "state":"uploaded",
              "size":{{checksum.Length}},
              "digest":"sha256:{{Sha(checksum)}}",
              "browser_download_url":"{{repository}}/releases/download/{{tag}}/{{ProductInformation.WindowsReleaseChecksumAssetName}}"
            }
          ]
        }
        """;
    }

    private static string Sha(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(value)
    };

    private static void RequireUpdateFailure(Action<bool, string> require, Action action, string message)
    {
        try
        {
            action();
            require(false, message);
        }
        catch (AppUpdateException)
        {
            // Oczekiwane zatrzymanie przez politykę aktualizacji.
        }
    }

    private sealed class TestProgress : IProgress<UpdateDownloadProgress>
    {
        private readonly Action<UpdateDownloadProgress> _report;
        public TestProgress(Action<UpdateDownloadProgress> report) => _report = report;
        public void Report(UpdateDownloadProgress value) => _report(value);
    }

    private sealed class UpdateTestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public UpdateTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
