using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class AppUpdateService : IDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultForcedCheckMinimumInterval = TimeSpan.FromMinutes(1);
    private readonly ApplicationLog _log;
    private readonly GitHubUpdateClient _client;
    private readonly string? _processPath;
    private readonly SemanticVersion _currentVersion;
    private readonly TimeSpan _forcedCheckMinimumInterval;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private AppUpdateSnapshot _snapshot;
    private GitHubReleaseInfo? _cachedRelease;
    private string? _etag;
    private long _checkGeneration;
    private DateTimeOffset? _lastCheckAttemptUtc;
    private DateTimeOffset? _blockedUntilUtc;
    private int _installationInProgress;
    private bool _installationHandedOff;
    private bool _disposed;

    public AppUpdateService(
        ApplicationLog log,
        GitHubUpdateClient? client = null,
        string? processPath = null,
        SemanticVersion? currentVersion = null,
        TimeSpan? forcedCheckMinimumInterval = null)
    {
        _log = log;
        _client = client ?? new GitHubUpdateClient();
        _processPath = processPath ?? Environment.ProcessPath;
        _currentVersion = currentVersion ?? ProductInformation.CurrentVersion;
        _forcedCheckMinimumInterval = forcedCheckMinimumInterval ?? DefaultForcedCheckMinimumInterval;
        ArgumentOutOfRangeException.ThrowIfLessThan(_forcedCheckMinimumInterval, TimeSpan.Zero);
        _snapshot = new AppUpdateSnapshot(_currentVersion, AppUpdatePhase.Idle);
    }

    public event EventHandler? StateChanged;

    public AppUpdateSnapshot GetSnapshot()
    {
        lock (_stateGate) return _snapshot;
    }

    public async Task<AppUpdateSnapshot> CheckForUpdatesAsync(bool force, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _installationInProgress) != 0) return GetSnapshot();
        var observedGeneration = Interlocked.Read(ref _checkGeneration);
        await _operationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _installationInProgress) != 0) return GetSnapshot();
            if (observedGeneration != Interlocked.Read(ref _checkGeneration)) return GetSnapshot();
            var previous = GetSnapshot();
            var now = DateTimeOffset.UtcNow;
            if (_blockedUntilUtc is { } blockedUntilUtc)
            {
                if (blockedUntilUtc > now) return previous;
                _blockedUntilUtc = null;
            }
            var minimumInterval = force ? _forcedCheckMinimumInterval : AutomaticCheckInterval;
            if (_lastCheckAttemptUtc is { } recentAttempt)
            {
                var sinceAttempt = now - recentAttempt;
                if (sinceAttempt >= TimeSpan.Zero && sinceAttempt < minimumInterval) return previous;
            }
            if (!force && previous.LastCheckedUtc is { } recentSuccess)
            {
                var sinceSuccess = now - recentSuccess;
                if (sinceSuccess >= TimeSpan.Zero && sinceSuccess < AutomaticCheckInterval) return previous;
            }

            _lastCheckAttemptUtc = now;

            SetSnapshot(previous with
            {
                Phase = AppUpdatePhase.Checking,
                Message = "Sprawdzanie aktualizacji…",
                ProgressPercent = null,
                HasError = false
            });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(MetadataTimeout);
                var result = await _client.GetLatestReleaseAsync(_etag, timeout.Token).ConfigureAwait(false);
                _blockedUntilUtc = null;
                if (!result.NotModified)
                {
                    _cachedRelease = result.Release ?? throw new AppUpdateException(
                        "GitHub nie zwrócił wydania.",
                        "GitHub nie zwrócił informacji o najnowszej wersji.");
                    _etag = result.ETag;
                }
                var latest = _cachedRelease ?? throw new AppUpdateException(
                    "Odpowiedź 304 otrzymano bez lokalnego cache wydania.",
                    "Nie udało się ustalić najnowszej wersji aplikacji.");
                var checkedAtUtc = DateTimeOffset.UtcNow;
                if (latest.Version.CompareTo(_currentVersion) > 0)
                {
                    SetSnapshot(new AppUpdateSnapshot(
                        _currentVersion,
                        AppUpdatePhase.Available,
                        latest,
                        $"Dostępna jest aktualizacja v{latest.Version}.",
                        LastCheckedUtc: checkedAtUtc));
                    _log.Info("Aktualizacja", $"Dostępna jest wersja v{latest.Version}; zainstalowana: v{_currentVersion}.");
                }
                else
                {
                    SetSnapshot(new AppUpdateSnapshot(
                        _currentVersion,
                        AppUpdatePhase.UpToDate,
                        Message: "Masz najnowszą wersję aplikacji.",
                        LastCheckedUtc: checkedAtUtc));
                    if (latest.Version.CompareTo(_currentVersion) < 0)
                        _log.Warning("Aktualizacja", $"GitHub latest wskazuje starszą wersję v{latest.Version}; downgrade został zablokowany.");
                    else
                        _log.Info("Aktualizacja", $"Wersja v{_currentVersion} jest aktualna.");
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                ApplyCheckFailure(new AppUpdateException(
                    "Sprawdzanie aktualizacji przekroczyło limit czasu.",
                    "GitHub nie odpowiedział na czas. Sprawdź aktualizacje ponownie później.",
                    exception));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return GetSnapshot();
            }
            catch (HttpRequestException exception)
            {
                ApplyCheckFailure(new AppUpdateException(
                    "Błąd sieci podczas sprawdzania aktualizacji.",
                    "Nie można połączyć się z GitHubem. Sprawdź internet i spróbuj ponownie później.",
                    exception));
            }
            catch (AppUpdateException exception)
            {
                RecordServerBlock(exception);
                ApplyCheckFailure(exception);
            }
            catch (Exception exception)
            {
                ApplyCheckFailure(new AppUpdateException(
                    "Nieoczekiwany błąd sprawdzania aktualizacji.",
                    "Nie udało się sprawdzić aktualizacji. Szczegóły zapisano w dzienniku.",
                    exception));
            }
            finally
            {
                Interlocked.Increment(ref _checkGeneration);
            }
            return GetSnapshot();
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public async Task PrepareAndLaunchUpdateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _installationInProgress, 1, 0) != 0)
            throw new AppUpdateException("Instalacja aktualizacji już trwa.", "Aktualizacja jest już pobierana lub instalowana.");

        string? sessionDirectory = null;
        var operationLockTaken = false;
        try
        {
            operationLockTaken = await _operationSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!operationLockTaken)
                throw new AppUpdateException(
                    "Nie można rozpocząć instalacji podczas sprawdzania aktualizacji.",
                    "Trwa sprawdzanie aktualizacji. Poczekaj chwilę i kliknij „Aktualizuj” ponownie.");

            if (_blockedUntilUtc is { } blockedUntilUtc && blockedUntilUtc > DateTimeOffset.UtcNow)
                throw new AppUpdateException(
                    $"GitHub wstrzymał kolejne żądania do {blockedUntilUtc:O}.",
                    "GitHub poprosił o przerwę. Spróbuj zainstalować aktualizację później.",
                    blockedUntilUtc - DateTimeOffset.UtcNow);

            var release = GetSnapshot().AvailableRelease;
            if (release is null || release.Version.CompareTo(_currentVersion) <= 0)
                throw new AppUpdateException("Brak nowszego wydania do instalacji.", "Nie ma obecnie nowszej wersji do zainstalowania.");
            if (string.IsNullOrWhiteSpace(_processPath))
                throw new AppUpdateException("Environment.ProcessPath jest pusty.", "Nie udało się odnaleźć działającego pliku aplikacji.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(InstallationTimeout);
            SetSnapshot(new AppUpdateSnapshot(
                _currentVersion,
                AppUpdatePhase.Downloading,
                release,
                $"Pobieranie aktualizacji v{release.Version}…",
                0,
                GetSnapshot().LastCheckedUtc));

            sessionDirectory = UpdateInstaller.CreateSessionDirectory(_processPath, release.Executable.Size);
            var checksumBytes = await _client.DownloadSmallAssetAsync(
                release,
                release.Checksum,
                GitHubReleasePolicy.MaximumChecksumBytes,
                timeout.Token).ConfigureAwait(false);
            var checksumHash = ReleaseChecksumParser.Parse(checksumBytes);
            if (!GitHubReleasePolicy.HashesEqual(checksumHash, release.Executable.Sha256Digest))
                throw new AppUpdateException(
                    "Suma z pliku .sha256 nie zgadza się z digestem assetu GitHub.",
                    "Nie udało się potwierdzić integralności aktualizacji. Instalacja została zatrzymana.");

            var candidatePath = Path.Combine(sessionDirectory, ProductInformation.WindowsReleaseAssetName);
            var progress = new InlineProgress<UpdateDownloadProgress>(value =>
            {
                var current = GetSnapshot();
                if (current.Phase != AppUpdatePhase.Downloading || current.ProgressPercent == value.Percent) return;
                SetSnapshot(current with
                {
                    Message = $"Pobieranie aktualizacji v{release.Version}: {value.Percent}%…",
                    ProgressPercent = value.Percent
                });
            });
            var downloadedHash = await _client.DownloadExecutableAsync(
                release,
                candidatePath,
                checksumHash,
                progress,
                timeout.Token).ConfigureAwait(false);

            SetSnapshot(new AppUpdateSnapshot(
                _currentVersion,
                AppUpdatePhase.Preparing,
                release,
                $"Przygotowywanie instalacji v{release.Version}…",
                100,
                GetSnapshot().LastCheckedUtc));
            await UpdateInstaller.PrepareAndStartHelperAsync(
                _processPath,
                candidatePath,
                release.Version,
                downloadedHash,
                _log,
                timeout.Token).ConfigureAwait(false);
            _installationHandedOff = true;
            SetSnapshot(new AppUpdateSnapshot(
                _currentVersion,
                AppUpdatePhase.ReadyToRestart,
                release,
                "Aktualizacja jest gotowa. Aplikacja uruchomi się ponownie.",
                100,
                GetSnapshot().LastCheckedUtc));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            var wrapped = new AppUpdateException(
                "Pobieranie lub przygotowanie aktualizacji przekroczyło limit czasu.",
                "Aktualizacja trwała zbyt długo i została zatrzymana. Spróbuj ponownie później.",
                exception);
            ApplyInstallFailure(wrapped);
            throw wrapped;
        }
        catch (HttpRequestException exception)
        {
            var wrapped = new AppUpdateException(
                "Błąd sieci podczas pobierania aktualizacji.",
                "Nie można pobrać aktualizacji. Sprawdź połączenie z internetem i spróbuj ponownie.",
                exception);
            ApplyInstallFailure(wrapped);
            throw wrapped;
        }
        catch (AppUpdateException exception)
        {
            RecordServerBlock(exception);
            ApplyInstallFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var wrapped = new AppUpdateException(
                "Nieoczekiwany błąd przygotowania aktualizacji.",
                "Nie udało się przygotować aktualizacji. Aplikacja nie została zmieniona.",
                exception);
            ApplyInstallFailure(wrapped);
            throw wrapped;
        }
        finally
        {
            if (!_installationHandedOff && sessionDirectory is not null) TryDeleteSession(sessionDirectory);
            if (operationLockTaken) _operationSemaphore.Release();
            if (!_installationHandedOff) Interlocked.Exchange(ref _installationInProgress, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _client.Dispose();
        StateChanged = null;
    }

    private void ApplyCheckFailure(AppUpdateException exception)
    {
        _log.Warning("Aktualizacja", exception.Message, exception);
        var previous = GetSnapshot();
        var available = previous.AvailableRelease;
        SetSnapshot(new AppUpdateSnapshot(
            _currentVersion,
            available is not null ? AppUpdatePhase.Available : AppUpdatePhase.Failed,
            available,
            exception.UserMessage,
            LastCheckedUtc: DateTimeOffset.UtcNow,
            HasError: true));
    }

    private void RecordServerBlock(AppUpdateException exception)
    {
        if (exception.RetryAfter is not { } retryAfter || retryAfter <= TimeSpan.Zero) return;
        var blockedUntilUtc = DateTimeOffset.UtcNow + retryAfter;
        if (_blockedUntilUtc is null || blockedUntilUtc > _blockedUntilUtc)
            _blockedUntilUtc = blockedUntilUtc;
    }

    private void ApplyInstallFailure(AppUpdateException exception)
    {
        _log.Error("Aktualizacja", exception.Message, exception);
        var release = GetSnapshot().AvailableRelease;
        SetSnapshot(new AppUpdateSnapshot(
            _currentVersion,
            AppUpdatePhase.Failed,
            release,
            exception.UserMessage,
            LastCheckedUtc: GetSnapshot().LastCheckedUtc,
            HasError: true));
    }

    private void SetSnapshot(AppUpdateSnapshot snapshot)
    {
        lock (_stateGate) _snapshot = snapshot;
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _log.Warning("Aktualizacja", "Odbiorca zmiany stanu aktualizacji zgłosił błąd.", exception);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDeleteSession(string sessionDirectory)
    {
        try
        {
            var parent = Directory.GetParent(sessionDirectory)?.FullName;
            if (parent is null || !string.Equals(Path.GetFileName(parent), ProductInformation.UpdateDirectoryName, StringComparison.Ordinal)) return;
            if (Directory.Exists(sessionDirectory) &&
                (File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(sessionDirectory, recursive: true);
        }
        catch
        {
            // Stary katalog zostanie usunięty przez CleanupStaleArtifacts przy kolejnym uruchomieniu.
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
