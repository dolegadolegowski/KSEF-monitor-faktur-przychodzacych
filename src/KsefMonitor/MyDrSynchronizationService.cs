using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Granica automatycznej synchronizacji zamienia każdy błąd na bezpieczny komunikat i zachowuje ostatni poprawny wynik.")]
internal sealed class MyDrSynchronizationService : IDisposable
{
    private static readonly TimeSpan BusyRetryInterval = TimeSpan.FromMinutes(1);
    private readonly AppStore _store;
    private readonly ApplicationLog _log;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _configurationGate = new();
    private readonly object _stateGate = new();
    private readonly object _scheduleGate = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource _configurationChanged = new();
    private MyDrCredentials? _credentials;
    private readonly MyDrState _state;
    private Timer? _timer;
    private DateTimeOffset? _nextScheduledSyncUtc;
    private int _configurationVersion;
    private int _isConfigured;
    private int _isSynchronizing;
    private int _activeOperations;
    private int _disposed;
    private int _disposeSetupCompleted;
    private int _primitivesDisposed;

    public MyDrSynchronizationService(AppStore store)
    {
        _shutdownToken = _shutdown.Token;
        _store = store;
        _log = store.Log;
        _credentials = store.LoadMyDrCredentials();
        Volatile.Write(ref _isConfigured, _credentials is { IsConfigured: true } ? 1 : 0);
        _state = store.LoadMyDrState();

        var connectionId = _credentials is { IsConfigured: true }
            ? _credentials.ConnectionId
            : Guid.Empty;
        if (_state.ConnectionId != connectionId)
        {
            _state.BindToConnection(connectionId);
            try
            {
                _store.SaveMyDrState(_state);
            }
            catch (Exception exception)
            {
                _state.LastError = UserFacingErrors.ForMyDrSynchronization(exception);
                _log.Error("MyDR", "Nie udało się przygotować lokalnego stanu MyDR. Monitor KSeF będzie działał dalej.", exception);
            }
        }
    }

    public bool IsSynchronizing => Volatile.Read(ref _isSynchronizing) != 0;

    public bool IsConfigured => Volatile.Read(ref _isConfigured) != 0;

    public event EventHandler<AppStatusMessage>? StatusChanged;
    public event EventHandler? StateChanged;

    public void Start()
    {
        if (IsDisposed) return;
        if (!IsConfigured)
        {
            CancelScheduledSync();
            return;
        }

        ScheduleNextAutomaticCheck();
    }

    public MyDrMonthSummary? GetMonthSummary(int year, int month)
    {
        var key = MyDrMonthKey.Create(year, month);
        lock (_stateGate)
            return _state.Months.TryGetValue(key, out var summary) ? summary.Snapshot() : null;
    }

    public MyDrSyncStatus GetStatusSnapshot()
    {
        bool configured;
        lock (_configurationGate) configured = _credentials is { IsConfigured: true };

        DateTimeOffset? next;
        lock (_scheduleGate) next = _nextScheduledSyncUtc;

        lock (_stateGate)
            return new MyDrSyncStatus(
                configured,
                IsSynchronizing,
                _state.LastCheckLocalDate,
                _state.LastAttemptUtc,
                _state.LastSuccessfulSyncUtc,
                next,
                _state.LastError);
    }

    public bool TryApplyConfigurationChange(
        Func<bool> persistChange,
        bool restartScheduler,
        out bool changed)
    {
        ArgumentNullException.ThrowIfNull(persistChange);
        changed = false;

        var entered = false;
        lock (_lifecycleGate)
        {
            if (IsDisposed) return false;
            entered = _syncGate.Wait(0);
            if (entered) _activeOperations++;
        }

        if (!entered) return false;

        CancelTimerWithoutNotification();
        var completed = false;
        try
        {
            changed = persistChange();
            if (changed) ReloadConfigurationFromStore();
            completed = true;
            return true;
        }
        finally
        {
            lock (_lifecycleGate) _syncGate.Release();
            CompleteOperation();
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (!IsDisposed && (restartScheduler || !completed)) Start();
        }
    }

    private void ReloadConfigurationFromStore()
    {
        var loaded = _store.LoadMyDrCredentials();
        CancellationTokenSource? obsoleteConfiguration = null;
        lock (_configurationGate)
        {
            if (!CredentialsEqual(_credentials, loaded))
            {
                _credentials = loaded;
                Volatile.Write(ref _isConfigured, loaded is { IsConfigured: true } ? 1 : 0);
                Interlocked.Increment(ref _configurationVersion);
                obsoleteConfiguration = _configurationChanged;
                _configurationChanged = new CancellationTokenSource();

                var connectionId = loaded is { IsConfigured: true } ? loaded.ConnectionId : Guid.Empty;
                lock (_stateGate)
                {
                    _state.BindToConnection(connectionId);
                    _store.SaveMyDrState(_state);
                }
            }
        }

        obsoleteConfiguration?.Cancel();
        obsoleteConfiguration?.Dispose();
    }

    public Task RefreshNowAsync(CancellationToken cancellationToken = default) =>
        SynchronizeAsync(manual: true, cancellationToken);

    private async void TimerElapsed(object? state)
    {
        try
        {
            await SynchronizeAsync(manual: false, _shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            // Zamykanie aplikacji.
        }
    }

    private async Task SynchronizeAsync(bool manual, CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var operationToken = operationCancellation.Token;
        bool entered;
        lock (_lifecycleGate)
        {
            operationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            entered = _syncGate.Wait(0, operationToken);
            if (entered)
            {
                _activeOperations++;
                Volatile.Write(ref _isSynchronizing, 1);
            }
        }

        if (!entered)
        {
            if (manual)
            {
                SetStatus("Odświeżanie MyDR już trwa.");
                throw new InvalidOperationException("Odświeżanie MyDR już trwa.");
            }
            if (IsSynchronizing) Schedule(BusyRetryInterval);
            return;
        }

        CancelTimerWithoutNotification();
        StateChanged?.Invoke(this, EventArgs.Empty);
        var configurationVersionAtStart = -1;
        var configurationToken = CancellationToken.None;
        CancellationTokenSource? linkedConfiguration = null;
        try
        {
            MyDrCredentials credentials;
            lock (_configurationGate)
            {
                if (_credentials is not { IsConfigured: true })
                    throw new InvalidOperationException("Brakuje kompletnych danych dostępowych MyDR.");
                configurationVersionAtStart = _configurationVersion;
                configurationToken = _configurationChanged.Token;
                credentials = _credentials.Snapshot();
            }

            linkedConfiguration = CancellationTokenSource.CreateLinkedTokenSource(operationToken, configurationToken);
            var syncToken = linkedConfiguration.Token;
            var today = MyDrDailySchedule.GetWarsawDate(DateTimeOffset.UtcNow);
            var alreadyChecked = WithCurrentState(configurationVersionAtStart, () =>
                _state.LastCheckLocalDate == today, syncToken);
            if (!manual && alreadyChecked)
            {
                _log.Info("MyDR", "Dzisiejsze automatyczne sprawdzenie zostało już wykonane.");
                return;
            }

            WithCurrentState(configurationVersionAtStart, () =>
            {
                _state.LastCheckLocalDate = today;
                _state.LastAttemptUtc = DateTimeOffset.UtcNow;
                _state.LastError = string.Empty;
                _store.SaveMyDrState(_state);
            }, syncToken);

            _log.Info("MyDR", manual
                ? "Rozpoczęto wymuszone odświeżanie obrotu MyDR."
                : "Rozpoczęto dzienne odświeżanie obrotu MyDR.");
            SetStatus("Pobieranie miesięcznego obrotu z MyDR…");

            using var client = new MyDrApiClient(
                credentials,
                rotatedRefreshTokenHandler: token => credentials = PersistRotatedRefreshToken(
                    configurationVersionAtStart,
                    credentials,
                    token,
                    syncToken));
            await client.AuthenticateAsync(syncToken).ConfigureAwait(false);

            var currentMonth = new DateOnly(today.Year, today.Month, 1);
            var firstVisibleMonth = currentMonth.AddMonths(-SynchronizationService.VisibleHistoryMonthsBack);
            var lastVisibleDate = currentMonth.AddMonths(1).AddDays(-1);
            var allVisits = new Dictionary<long, MyDrVisit>();

            for (var offset = 0; offset <= SynchronizationService.VisibleHistoryMonthsBack; offset++)
            {
                var month = firstVisibleMonth.AddMonths(offset);
                var lastDay = month.AddMonths(1).AddDays(-1);
                SetStatus($"Pobieranie obrotu MyDR: {month.ToString("MM.yyyy", CultureInfo.InvariantCulture)}…");
                var visits = await client.GetPrivateVisitsAsync(month, lastDay, syncToken).ConfigureAwait(false);
                foreach (var visit in visits)
                    if (!allVisits.TryAdd(visit.Id, visit))
                        throw new MyDrApiException("MyDR zwrócił tę samą wizytę w więcej niż jednym miesiącu.");
            }

            var oldCache = WithCurrentState(configurationVersionAtStart, () =>
                _state.Visits.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()), syncToken);
            var newCache = new Dictionary<long, MyDrCachedVisit>();
            var performedVisits = allVisits.Values
                .Where(visit => visit.IsPerformed)
                .Select(visit => (Visit: visit, Date: visit.GetDate()))
                .Where(item => item.Date >= firstVisibleMonth && item.Date <= lastVisibleDate)
                .OrderBy(item => item.Date)
                .ThenBy(item => item.Visit.Id)
                .ToList();

            for (var index = 0; index < performedVisits.Count; index++)
            {
                syncToken.ThrowIfCancellationRequested();
                var item = performedVisits[index];
                var state = item.Visit.State?.Trim() ?? string.Empty;
                MyDrCachedVisit cached;
                var isCurrentMonth = item.Date.Year == currentMonth.Year && item.Date.Month == currentMonth.Month;
                if (!manual &&
                    !isCurrentMonth &&
                    !string.IsNullOrWhiteSpace(item.Visit.LatestModification) &&
                    oldCache.TryGetValue(item.Visit.Id, out var existing) &&
                    existing.VisitDate == item.Date &&
                    string.Equals(existing.State, state, StringComparison.Ordinal) &&
                    string.Equals(existing.LatestModification, item.Visit.LatestModification, StringComparison.Ordinal))
                {
                    cached = existing.Snapshot();
                }
                else
                {
                    SetStatus($"Obliczanie obrotu MyDR: {index + 1}/{performedVisits.Count}…");
                    var services = await client.GetVisitServicesAsync(item.Visit.Id, syncToken).ConfigureAwait(false);
                    decimal gross = 0;
                    checked
                    {
                        foreach (var service in services) gross += MyDrApiClient.GetServiceGrossValue(service);
                    }

                    cached = new MyDrCachedVisit
                    {
                        VisitId = item.Visit.Id,
                        VisitDate = item.Date,
                        State = state,
                        LatestModification = item.Visit.LatestModification,
                        GrossAmount = gross,
                        ServiceCount = services.Count
                    };
                }

                newCache[item.Visit.Id] = cached;
            }

            var completedAtUtc = DateTimeOffset.UtcNow;
            var summaries = new Dictionary<string, MyDrMonthSummary>(StringComparer.Ordinal);
            for (var offset = 0; offset <= SynchronizationService.VisibleHistoryMonthsBack; offset++)
            {
                var month = firstVisibleMonth.AddMonths(offset);
                var monthVisits = newCache.Values
                    .Where(visit => visit.VisitDate.Year == month.Year && visit.VisitDate.Month == month.Month)
                    .ToList();
                summaries[MyDrMonthKey.Create(month.Year, month.Month)] = new MyDrMonthSummary
                {
                    Year = month.Year,
                    Month = month.Month,
                    GrossAmount = monthVisits.Sum(visit => visit.GrossAmount),
                    VisitCount = monthVisits.Count,
                    ServiceCount = monthVisits.Sum(visit => visit.ServiceCount),
                    UpdatedAtUtc = completedAtUtc
                };
            }

            WithCurrentState(configurationVersionAtStart, () =>
            {
                var candidate = _state.Snapshot();
                candidate.Visits = newCache;
                candidate.Months = summaries;
                candidate.LastSuccessfulSyncUtc = completedAtUtc;
                candidate.LastError = string.Empty;
                _store.SaveMyDrState(candidate);

                // Stan widoczny dla UI zmieniamy dopiero po trwałym zapisie
                // kompletnej migawki. Błąd dysku zachowuje poprzednie kwoty.
                _state.Visits = candidate.Visits;
                _state.Months = candidate.Months;
                _state.LastSuccessfulSyncUtc = candidate.LastSuccessfulSyncUtc;
                _state.LastError = candidate.LastError;
            }, syncToken);

            _log.Info("MyDR", $"Zakończono odświeżanie obrotu. Wizyty: {newCache.Count}; miesiące: {summaries.Count}.");
            SetStatus($"Obrót MyDR odświeżono o {DateTime.Now:HH:mm}.");
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            _log.Info("MyDR", "Odświeżanie MyDR zostało zatrzymane.");
            if (manual && !_shutdownToken.IsCancellationRequested) throw;
        }
        catch (OperationCanceledException) when (
            configurationToken.IsCancellationRequested || ConfigurationHasChanged(configurationVersionAtStart))
        {
            _log.Info("MyDR", "Odświeżanie MyDR przerwano z powodu zmiany danych dostępowych.");
        }
        catch (Exception exception)
        {
            var message = UserFacingErrors.ForMyDrSynchronization(exception);
            _log.Error("MyDR", "Nie udało się odświeżyć miesięcznego obrotu.", exception);
            TryRecordError(configurationVersionAtStart, message);
            SetStatus(message, StatusSeverity.Error);
            if (manual) throw;
        }
        finally
        {
            linkedConfiguration?.Dispose();
            lock (_lifecycleGate)
            {
                Volatile.Write(ref _isSynchronizing, 0);
                _syncGate.Release();
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            CompleteOperation();
            if (!IsDisposed) Start();
        }
    }

    private MyDrCredentials PersistRotatedRefreshToken(
        int configurationVersion,
        MyDrCredentials credentials,
        string? rotatedRefreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rotatedRefreshToken) ||
            string.Equals(credentials.RefreshToken, rotatedRefreshToken, StringComparison.Ordinal))
            return credentials;

        cancellationToken.ThrowIfCancellationRequested();
        lock (_configurationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configurationVersion != _configurationVersion ||
                _credentials is null ||
                _credentials.ConnectionId != credentials.ConnectionId)
                throw new OperationCanceledException("Dane dostępowe MyDR zmieniły się podczas synchronizacji.", cancellationToken);

            var updated = new MyDrCredentials
            {
                ConnectionId = credentials.ConnectionId,
                ClientId = credentials.ClientId,
                ClientSecret = credentials.ClientSecret,
                RefreshToken = rotatedRefreshToken
            };
            if (!_store.TryReplaceMyDrCredentials(credentials.ConnectionId, updated))
            {
                _log.Info("MyDR", "Pominięto zapis odnowionego Refresh Tokena, ponieważ ustawienia zostały zmienione.");
                ReloadConfigurationFromStore();
                throw new OperationCanceledException("Dane dostępowe MyDR zmieniły się podczas synchronizacji.", cancellationToken);
            }
            _credentials = updated.Snapshot();
            _log.Info("MyDR", "Bezpiecznie zapisano odnowiony Refresh Token.");
            return updated;
        }
    }

    private T WithCurrentState<T>(int configurationVersion, Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_configurationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configurationVersion != _configurationVersion)
                throw new OperationCanceledException("Dane dostępowe MyDR zmieniły się podczas synchronizacji.", cancellationToken);
            lock (_stateGate) return action();
        }
    }

    private void WithCurrentState(int configurationVersion, Action action, CancellationToken cancellationToken) =>
        WithCurrentState(configurationVersion, () =>
        {
            action();
            return true;
        }, cancellationToken);

    private void TryRecordError(int configurationVersion, string message)
    {
        if (configurationVersion < 0) return;
        lock (_configurationGate)
        {
            if (configurationVersion != _configurationVersion) return;
            lock (_stateGate)
            {
                _state.LastError = message;
                try
                {
                    _store.SaveMyDrState(_state);
                }
                catch (Exception persistenceException)
                {
                    _log.Error("MyDR", "Nie udało się zapisać informacji o błędzie synchronizacji.", persistenceException);
                }
            }
        }
    }

    private void ScheduleNextAutomaticCheck()
    {
        DateOnly? lastCheck;
        lock (_stateGate) lastCheck = _state.LastCheckLocalDate;
        var next = MyDrDailySchedule.GetNextCheckUtc(DateTimeOffset.UtcNow, lastCheck);
        ScheduleAt(next);
    }

    private void Schedule(TimeSpan due)
    {
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        ScheduleAt(DateTimeOffset.UtcNow + due);
    }

    private void ScheduleAt(DateTimeOffset nextUtc)
    {
        var configurationVersion = Volatile.Read(ref _configurationVersion);
        if (IsDisposed || _shutdownToken.IsCancellationRequested || !IsConfigured) return;
        var due = nextUtc - DateTimeOffset.UtcNow;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        lock (_scheduleGate)
        {
            if (IsDisposed ||
                _shutdownToken.IsCancellationRequested ||
                !IsConfigured ||
                configurationVersion != Volatile.Read(ref _configurationVersion))
                return;

            _nextScheduledSyncUtc = nextUtc;
            _timer?.Dispose();
            _timer = new Timer(TimerElapsed, null, due, Timeout.InfiniteTimeSpan);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelScheduledSync()
    {
        CancelTimerWithoutNotification();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelTimerWithoutNotification()
    {
        lock (_scheduleGate)
        {
            _nextScheduledSyncUtc = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static bool CredentialsEqual(MyDrCredentials? left, MyDrCredentials? right) =>
        left?.ConnectionId == right?.ConnectionId &&
        string.Equals(left?.ClientId, right?.ClientId, StringComparison.Ordinal) &&
        string.Equals(left?.ClientSecret, right?.ClientSecret, StringComparison.Ordinal) &&
        string.Equals(left?.RefreshToken, right?.RefreshToken, StringComparison.Ordinal);

    private bool ConfigurationHasChanged(int version)
    {
        if (version < 0) return false;
        lock (_configurationGate) return version != _configurationVersion;
    }

    private void SetStatus(string text, StatusSeverity severity = StatusSeverity.Information) =>
        StatusChanged?.Invoke(this, new AppStatusMessage(text, severity));

    private void CompleteOperation()
    {
        var disposePrimitives = false;
        lock (_lifecycleGate)
        {
            _activeOperations--;
            disposePrimitives = IsDisposed && _disposeSetupCompleted != 0 && _activeOperations == 0;
        }
        if (disposePrimitives) DisposeSynchronizationPrimitives();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        }

        _shutdown.Cancel();
        CancelTimerWithoutNotification();
        lock (_configurationGate) _configurationChanged.Cancel();

        bool disposePrimitivesNow;
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _disposeSetupCompleted, 1);
            disposePrimitivesNow = _activeOperations == 0;
        }
        if (disposePrimitivesNow) DisposeSynchronizationPrimitives();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void DisposeSynchronizationPrimitives()
    {
        if (Interlocked.Exchange(ref _primitivesDisposed, 1) != 0) return;
        lock (_configurationGate) _configurationChanged.Dispose();
        _shutdown.Dispose();
        _syncGate.Dispose();
    }
}
