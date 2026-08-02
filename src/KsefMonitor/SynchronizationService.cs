using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class InvoiceContentPendingException : Exception
{
    public InvoiceContentPendingException(DateTimeOffset retryAtUtc)
        : base("Limit pobierania pełnej treści faktur zostanie odnowiony później.")
    {
        RetryAtUtc = retryAtUtc;
    }

    public DateTimeOffset RetryAtUtc { get; }
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Zamykanie aplikacji nie może zostać przerwane przez błąd końcowego zapisu cache.")]
internal sealed class SynchronizationService : IDisposable
{
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);
    public const int VisibleHistoryMonthsBack = 3;
    private static readonly TimeSpan FailureRetryInterval = SyncInterval;
    private static readonly TimeSpan BusyRetryInterval = TimeSpan.FromMinutes(1);
    private const int MaxInvoiceDownloadsPerHour = 60;

    private readonly AppStore _store;
    private readonly ApplicationLog _log;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _stateGate = new();
    private readonly object _configurationGate = new();
    private readonly object _scheduleGate = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource _configurationChanged = new();
    private Timer? _timer;
    private KsefApiClient? _client;
    private AppSettings _settings;
    private string? _token;
    private int _configurationVersion;
    private int _clientConfigurationVersion = -1;
    private DateTimeOffset? _nextScheduledSyncUtc;
    private int _isSynchronizing;
    private int _activeOperations;
    private int _disposed;
    private int _disposeSetupCompleted;
    private int _primitivesDisposed;

    public SynchronizationService(AppStore store)
    {
        _shutdownToken = _shutdown.Token;
        _store = store;
        _log = store.Log;
        _settings = store.LoadSettings();
        _token = store.LoadToken();
        State = store.LoadState();
        var persistedAttempts = store.LoadDownloadAttempts();
        if (persistedAttempts is not null)
            State.InvoiceDownloadAttemptsUtc = persistedAttempts;
        else
            store.SaveDownloadAttempts(State.InvoiceDownloadAttemptsUtc);

        var normalizedNip = NipValidator.IsValid(_settings.Nip) ? NipValidator.Normalize(_settings.Nip) : string.Empty;
        var stateChanged = State.BindToContext(normalizedNip);
        if (State.HistoryMonthsBack < VisibleHistoryMonthsBack)
        {
            // Po aktualizacji zakresu historii cofamy HWM dokładnie raz. Inaczej
            // istniejący cache pobierałby wyłącznie nowe dokumenty i starsze
            // miesiące pozostałyby puste mimo dostępnej nawigacji.
            var hasExistingBaseline = State.PermanentStorageHwmDate is not null
                                      || State.LastSuccessfulSyncUtc is not null
                                      || State.Invoices.Count > 0;
            if (hasExistingBaseline)
            {
                var previousMonthsBack = Math.Max(1, State.HistoryMonthsBack);
                var firstCurrentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
                State.HistoricalBackfillBeforeIssueDate = firstCurrentMonth.AddMonths(-previousMonthsBack);
            }
            State.HistoryMonthsBack = VisibleHistoryMonthsBack;
            State.LastSuccessfulSyncUtc = null;
            State.LastMetadataSyncAttemptUtc = null;
            State.PermanentStorageHwmDate = null;
            stateChanged = true;
        }
        if (stateChanged) store.SaveState(State);
    }

    private AppState State { get; }
    public bool IsSynchronizing => Volatile.Read(ref _isSynchronizing) != 0;
    public bool IsConfigured
    {
        get
        {
            lock (_configurationGate) return _settings.IsConfigured && !string.IsNullOrWhiteSpace(_token);
        }
    }
    public DateTimeOffset? NextScheduledSyncUtc
    {
        get
        {
            lock (_scheduleGate) return _nextScheduledSyncUtc;
        }
    }

    public event EventHandler<AppStatusMessage>? StatusChanged;
    public event EventHandler? StateChanged;
    public event EventHandler<IReadOnlyList<StoredInvoice>>? NewInvoicesDiscovered;

    public void Start()
    {
        if (IsDisposed) return;
        if (!IsConfigured)
        {
            CancelScheduledSync();
            SetStatus("Skonfiguruj NIP i token KSeF.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var sinceLast = GetLastSuccessfulSyncUtc() is { } last ? now - last : SyncInterval;
        // Po cofnięciu zegara systemowego nie odkładamy synchronizacji na wiele godzin.
        var due = sinceLast < TimeSpan.Zero || sinceLast >= SyncInterval
            ? TimeSpan.Zero
            : SyncInterval - sinceLast;
        Schedule(due);
    }

    public IReadOnlyList<StoredInvoice> GetInvoicesSnapshot()
    {
        lock (_stateGate) return State.Invoices.Values.Select(invoice => invoice.Snapshot()).ToList();
    }

    public bool TryGetInvoice(string ksefNumber, out StoredInvoice? invoice)
    {
        lock (_stateGate)
        {
            if (!State.Invoices.TryGetValue(ksefNumber, out var stored))
            {
                invoice = null;
                return false;
            }
            invoice = stored.Snapshot();
            return true;
        }
    }

    public DateTimeOffset? GetLastSuccessfulSyncUtc()
    {
        lock (_stateGate) return State.LastSuccessfulSyncUtc;
    }

    public void UpdateConfiguration()
    {
        var newSettings = _store.LoadSettings();
        var newToken = _store.LoadToken();
        CancellationTokenSource? obsoleteConfiguration = null;
        bool connectionChanged;
        bool contextChanged;
        bool synchronizing;
        lock (_configurationGate)
        {
            var oldNip = NipValidator.Normalize(_settings.Nip);
            var newNip = NipValidator.Normalize(newSettings.Nip);
            connectionChanged = !string.Equals(oldNip, newNip, StringComparison.Ordinal)
                                || !string.Equals(_token, newToken, StringComparison.Ordinal)
                                || _settings.RequiresProductionToken != newSettings.RequiresProductionToken;
            contextChanged = !string.Equals(oldNip, newNip, StringComparison.Ordinal);
            _settings = newSettings;
            _token = newToken;
            synchronizing = IsSynchronizing;

            if (connectionChanged)
            {
                _configurationVersion++;
                obsoleteConfiguration = _configurationChanged;
                _configurationChanged = new CancellationTokenSource();
                if (!synchronizing)
                {
                    _client?.Dispose();
                    _client = null;
                    _clientConfigurationVersion = -1;
                }
            }

            if (contextChanged && !string.IsNullOrWhiteSpace(newNip))
            {
                lock (_stateGate)
                {
                    if (State.BindToContext(newNip)) _store.SaveState(State);
                }
            }
        }

        obsoleteConfiguration?.Cancel();
        obsoleteConfiguration?.Dispose();
        if (contextChanged) StateChanged?.Invoke(this, EventArgs.Empty);

        if (connectionChanged && synchronizing)
        {
            SetStatus("Ustawienia zapisane. Zostaną użyte po zakończeniu bieżącej synchronizacji.");
            return;
        }

        if (connectionChanged) Start();
        else SetStatus("Ustawienia zapisane.");
    }

    public async Task RefreshNowAsync()
    {
        await SynchronizeAsync(manual: true, _shutdownToken).ConfigureAwait(false);
    }

    public async Task<StoredInvoice?> EnsureInvoiceXmlAsync(string ksefNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ksefNumber)) throw new ArgumentException("Numer KSeF nie może być pusty.", nameof(ksefNumber));
        if (TryGetInvoice(ksefNumber, out var cached) && cached is not null && !string.IsNullOrWhiteSpace(cached.Xml))
            return cached;

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var operationToken = operationCancellation.Token;
        RegisterOperation(operationToken);

        var gateEntered = false;
        var configurationVersionAtStart = -1;
        var configurationToken = CancellationToken.None;
        CancellationTokenSource? linkedConfiguration = null;
        try
        {
            await _syncGate.WaitAsync(operationToken).ConfigureAwait(false);
            gateEntered = true;
            Volatile.Write(ref _isSynchronizing, 1);
            StateChanged?.Invoke(this, EventArgs.Empty);

            KsefApiClient client;
            lock (_configurationGate)
            {
                if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(_token))
                    throw new InvalidOperationException("Brak poprawnego NIP-u lub produkcyjnego tokena KSeF.");

                configurationVersionAtStart = _configurationVersion;
                configurationToken = _configurationChanged.Token;
                if (_client is null || _clientConfigurationVersion != configurationVersionAtStart)
                {
                    _client?.Dispose();
                    _client = new KsefApiClient(_settings, _token);
                    _clientConfigurationVersion = configurationVersionAtStart;
                }
                client = _client;
            }

            linkedConfiguration = CancellationTokenSource.CreateLinkedTokenSource(operationToken, configurationToken);
            var downloadToken = linkedConfiguration.Token;
            var prepared = WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                if (!State.Invoices.TryGetValue(ksefNumber, out var invoice))
                    return (Invoice: (StoredInvoice?)null, RetryAtUtc: (DateTimeOffset?)null, Exists: false);
                if (!string.IsNullOrWhiteSpace(invoice.Xml))
                    return (Invoice: invoice.Snapshot(), RetryAtUtc: (DateTimeOffset?)null, Exists: true);
                return (Invoice: (StoredInvoice?)null, RetryAtUtc: ReserveInvoiceDownloadLocked(), Exists: true);
            }, downloadToken);

            if (!prepared.Exists) return null;
            if (prepared.Invoice is not null) return prepared.Invoice;
            if (prepared.RetryAtUtc is { } retryAtUtc) throw new InvoiceContentPendingException(retryAtUtc);

            SetStatus("Pobieranie pełnej treści faktury…");
            _log.Info("Pobieranie faktur", $"Rozpoczęto priorytetowe pobieranie treści faktury {ksefNumber}.");
            var xml = await client.DownloadInvoiceXmlAsync(ksefNumber, downloadToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("KSeF zwrócił pustą treść faktury.");
            downloadToken.ThrowIfCancellationRequested();

            Task persistence = Task.CompletedTask;
            var refreshed = WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                if (!State.Invoices.TryGetValue(ksefNumber, out var invoice)) return null;
                invoice.Xml = xml;
                persistence = _store.SaveStateAsync(State);
                return invoice.Snapshot();
            }, downloadToken);
            await persistence.ConfigureAwait(false);
            _log.Info("Pobieranie faktur", $"Zapisano pełną treść faktury {ksefNumber} w lokalnej pamięci.");
            SetStatus("Pełna treść faktury jest gotowa.");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return refreshed;
        }
        catch (InvoiceContentPendingException exception)
        {
            _log.Info("Pobieranie faktur", $"Pobieranie pełnej treści wstrzymane do {exception.RetryAtUtc:O} z powodu lokalnego limitu.");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (configurationToken.IsCancellationRequested)
        {
            _log.Info("Pobieranie faktur", "Pobieranie pełnej treści przerwano z powodu zmiany ustawień.");
            throw;
        }
        catch (KsefApiException exception)
        {
            _log.Warning("Pobieranie faktur", "Nie udało się priorytetowo pobrać pełnej treści faktury.", exception);
            SetStatus(UserFacingErrors.ForSynchronization(exception), StatusSeverity.Error);
            if (exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                RecordInvoiceDownloadBlock(configurationVersionAtStart, exception.RetryAfter);
                Schedule(MaxDelay(SyncInterval, exception.RetryAfter));
            }
            throw;
        }
        catch (Exception exception)
        {
            _log.Error("Pobieranie faktur", "Nie udało się priorytetowo pobrać pełnej treści faktury.", exception);
            SetStatus("Nie udało się pobrać pełnych danych faktury. Spróbuj ponownie.", StatusSeverity.Error);
            throw;
        }
        finally
        {
            linkedConfiguration?.Dispose();
            if (gateEntered)
            {
                Volatile.Write(ref _isSynchronizing, 0);
                _syncGate.Release();
            }

            var configurationChanged = false;
            lock (_configurationGate)
            {
                configurationChanged = configurationVersionAtStart >= 0 &&
                                       configurationVersionAtStart != _configurationVersion;
                if ((configurationChanged || IsDisposed) &&
                    _clientConfigurationVersion == configurationVersionAtStart)
                {
                    _client?.Dispose();
                    _client = null;
                    _clientConfigurationVersion = -1;
                }
            }
            if (configurationChanged && !IsDisposed)
            {
                SetStatus("Zastosowano nowe ustawienia. Rozpoczynanie synchronizacji…");
                Start();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            CompleteOperation();
        }
    }

    public void MarkViewed(string ksefNumber)
    {
        Task persistence;
        lock (_stateGate)
        {
            if (!State.Invoices.TryGetValue(ksefNumber, out var invoice)) return;
            if (invoice.ViewedAtUtc is not null) return;
            invoice.ViewedAtUtc = DateTimeOffset.UtcNow;
            persistence = _store.SaveStateAsync(State);
        }
        ObserveBackgroundPersistence(persistence);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkNotified(IEnumerable<string> ksefNumbers)
    {
        Task persistence;
        lock (_stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var number in ksefNumbers)
                if (State.Invoices.TryGetValue(number, out var invoice) && invoice.NotifiedAtUtc is null)
                {
                    invoice.NotifiedAtUtc = now;
                    changed = true;
                }
            if (!changed) return;
            persistence = _store.SaveStateAsync(State);
        }
        ObserveBackgroundPersistence(persistence);
    }

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
        bool entered;
        lock (_lifecycleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            entered = _syncGate.Wait(0, cancellationToken);
            if (entered)
            {
                _activeOperations++;
                Volatile.Write(ref _isSynchronizing, 1);
            }
        }
        if (!entered)
        {
            if (manual) SetStatus("Synchronizacja już trwa.");
            else Schedule(BusyRetryInterval);
            return;
        }

        _log.Info("Synchronizacja", manual ? "Rozpoczęto ręczne odświeżanie faktur." : "Rozpoczęto automatyczne odświeżanie faktur.");
        lock (_scheduleGate) _nextScheduledSyncUtc = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        var configurationVersionAtStart = -1;
        var configurationToken = CancellationToken.None;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            KsefApiClient client;
            lock (_configurationGate)
            {
                if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(_token))
                    throw new InvalidOperationException("Brak poprawnego NIP-u lub produkcyjnego tokena KSeF.");

                configurationVersionAtStart = _configurationVersion;
                configurationToken = _configurationChanged.Token;
                if (_client is null || _clientConfigurationVersion != configurationVersionAtStart)
                {
                    _client?.Dispose();
                    _client = new KsefApiClient(_settings, _token);
                    _clientConfigurationVersion = configurationVersionAtStart;
                }
                client = _client;
            }
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, configurationToken);
            var syncToken = linkedCancellation.Token;
            SetStatus("Łączenie z KSeF…");

            var queryPlan = WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                var savedHwm = State.PermanentStorageHwmDate;
                var initialFrom = GetInitialFromDate();
                var from = savedHwm is { } hwm && hwm > initialFrom ? hwm : initialFrom;
                State.LastMetadataSyncAttemptUtc = DateTimeOffset.UtcNow;
                _store.SaveState(State);
                return (From: from, UsesSavedHwm: savedHwm is not null && from == savedHwm);
            }, syncToken);
            MetadataQueryResult result;
            var recoveredHwm = false;
            try
            {
                result = await client.QueryReceivedInvoicesAsync(queryPlan.From, syncToken).ConfigureAwait(false);
            }
            catch (KsefApiException exception) when (queryPlan.UsesSavedHwm && exception.HasErrorCode(21183))
            {
                // Rzadko serwer może uznać zapisany wcześniej HWM za późniejszy od
                // aktualnego punktu. Odbudowanie widocznego zakresu jest bezpieczne,
                // bo faktury są deduplikowane po numerze KSeF.
                SetStatus("Odtwarzanie punktu synchronizacji KSeF…");
                _log.Warning("Synchronizacja", "Zapisany punkt HWM został odrzucony. Odtwarzanie widocznego zakresu.", exception);
                result = await client.QueryReceivedInvoicesAsync(GetInitialFromDate(), syncToken).ConfigureAwait(false);
                recoveredHwm = true;
            }
            syncToken.ThrowIfCancellationRequested();

            // Metadane, HWM i czyszczenie zakresu są jednym atomowym zatwierdzeniem
            // dla konkretnego NIP-u. Zmiana konfiguracji nie może domieszać danych.
            var newInvoices = WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                var added = MergeMetadataLocked(result.Invoices);
                PruneInvoicesOutsideVisibleRangeLocked();
                if (recoveredHwm)
                    State.PermanentStorageHwmDate = result.PermanentStorageHwmDate;
                else if (result.PermanentStorageHwmDate is { } hwm &&
                         (State.PermanentStorageHwmDate is null || hwm > State.PermanentStorageHwmDate))
                    State.PermanentStorageHwmDate = hwm;
                State.HistoricalBackfillBeforeIssueDate = null;
                _store.SaveState(State);
                return added.Select(invoice => invoice.Snapshot()).ToList();
            }, syncToken);
            StateChanged?.Invoke(this, EventArgs.Empty);

            var downloadNow = DateTimeOffset.UtcNow;
            var downloadWindowStart = downloadNow.AddHours(-1);
            var downloadWindowFutureLimit = downloadNow.AddMinutes(5);
            var pendingXml = WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                if (State.InvoiceDownloadBlockedUntilUtc is { } blockedUntilUtc)
                {
                    if (blockedUntilUtc > downloadNow) return new List<StoredInvoice>();
                    State.InvoiceDownloadBlockedUntilUtc = null;
                }
                State.InvoiceDownloadAttemptsUtc.RemoveAll(x => x <= downloadWindowStart || x > downloadWindowFutureLimit);
                var availableDownloads = Math.Max(0, MaxInvoiceDownloadsPerHour - State.InvoiceDownloadAttemptsUtc.Count);
                return State.Invoices.Values
                    .Where(x => string.IsNullOrWhiteSpace(x.Xml))
                    .OrderByDescending(x => x.IsNew)
                    .ThenBy(x => x.DiscoveredAtUtc)
                    .Take(availableDownloads)
                    .ToList();
            }, syncToken);

            var xmlFailures = 0;
            TimeSpan? xmlRetryAfter = null;
            for (var index = 0; index < pendingXml.Count; index++)
            {
                syncToken.ThrowIfCancellationRequested();
                if (index > 0) await Task.Delay(TimeSpan.FromSeconds(4), syncToken).ConfigureAwait(false);
                var invoice = pendingXml[index];
                SetStatus($"Pobieranie treści faktur: {index + 1}/{pendingXml.Count}…");
                WithCurrentConfiguration(configurationVersionAtStart, () =>
                {
                    State.InvoiceDownloadAttemptsUtc.Add(DateTimeOffset.UtcNow);
                    _store.SaveDownloadAttempts(State.InvoiceDownloadAttemptsUtc);
                }, syncToken);
                try
                {
                    var xml = await client.DownloadInvoiceXmlAsync(invoice.KsefNumber, syncToken).ConfigureAwait(false);
                    syncToken.ThrowIfCancellationRequested();
                    WithCurrentConfiguration(configurationVersionAtStart, () =>
                    {
                        invoice.Xml = xml;
                        if ((index + 1) % 5 == 0 || index == pendingXml.Count - 1)
                            _store.SaveState(State);
                    }, syncToken);
                }
                catch (KsefApiException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    xmlFailures += pendingXml.Count - index;
                    xmlRetryAfter = exception.RetryAfter;
                    RecordInvoiceDownloadBlock(configurationVersionAtStart, exception.RetryAfter);
                    _log.Warning("Pobieranie faktur", "KSeF ograniczył pobieranie treści faktur. Kolejka zostanie wznowiona później.", exception);
                    break;
                }
                catch (KsefApiException exception)
                {
                    // Metadane pozostają dostępne. XML zostanie ponowiony w następnym cyklu.
                    xmlFailures++;
                    _log.Warning("Pobieranie faktur", "Nie udało się pobrać treści jednej faktury. Metadane pozostały dostępne.", exception);
                }
            }

            WithCurrentConfiguration(configurationVersionAtStart, () =>
            {
                State.LastSuccessfulSyncUtc = DateTimeOffset.UtcNow;
                _store.SaveState(State);
            }, syncToken);
            var remainingXmlCount = WithCurrentConfiguration(configurationVersionAtStart, () =>
                State.Invoices.Values.Count(invoice => string.IsNullOrWhiteSpace(invoice.Xml)), syncToken);
            var listStatus = newInvoices.Count == 0 ? "Brak nowych faktur." : $"Nowe faktury: {newInvoices.Count}.";
            var xmlStatus = remainingXmlCount == 0 ? string.Empty : $" Treść {remainingXmlCount} faktur oczekuje na pobranie.";
            SetStatus($"{listStatus}{xmlStatus} Odświeżono {DateTime.Now:HH:mm}.");
            _log.Info("Synchronizacja", $"Zakończono odświeżanie. Nowe faktury: {newInvoices.Count}; błędy pobierania XML: {xmlFailures}; oczekujące treści XML: {remainingXmlCount}.");
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (newInvoices.Count > 0) NewInvoicesDiscovered?.Invoke(this, newInvoices);
            Schedule(MaxDelay(SyncInterval, xmlRetryAfter));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Synchronizacja zatrzymana.");
            _log.Info("Synchronizacja", "Synchronizacja została zatrzymana podczas zamykania aplikacji.");
        }
        catch (OperationCanceledException) when (configurationToken.IsCancellationRequested)
        {
            SetStatus("Synchronizacja została zatrzymana z powodu zmiany ustawień.");
            _log.Info("Synchronizacja", "Synchronizacja została zatrzymana z powodu zmiany ustawień.");
        }
        catch (KsefApiException exception)
        {
            _log.Error("Synchronizacja", "KSeF zwrócił błąd podczas odświeżania faktur.", exception);
            SetStatus(UserFacingErrors.ForSynchronization(exception), StatusSeverity.Error);
            Schedule(MaxDelay(FailureRetryInterval, exception.RetryAfter));
            if (manual) throw;
        }
        catch (Exception exception)
        {
            _log.Error("Synchronizacja", "Nie udało się odświeżyć faktur.", exception);
            SetStatus(UserFacingErrors.ForSynchronization(exception), StatusSeverity.Error);
            Schedule(FailureRetryInterval);
            if (manual) throw;
        }
        finally
        {
            linkedCancellation?.Dispose();
            lock (_lifecycleGate)
            {
                Volatile.Write(ref _isSynchronizing, 0);
                _syncGate.Release();
            }
            var configurationChanged = false;
            lock (_configurationGate)
            {
                configurationChanged = configurationVersionAtStart >= 0 && configurationVersionAtStart != _configurationVersion;
                if ((configurationChanged || IsDisposed) &&
                    _clientConfigurationVersion == configurationVersionAtStart)
                {
                    _client?.Dispose();
                    _client = null;
                    _clientConfigurationVersion = -1;
                }
            }
            if (configurationChanged && !IsDisposed)
            {
                SetStatus("Zastosowano nowe ustawienia. Rozpoczynanie synchronizacji…");
                Start();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            CompleteOperation();
        }
    }

    private List<StoredInvoice> MergeMetadataLocked(IEnumerable<InvoiceMetadata> metadata)
    {
        var added = new List<StoredInvoice>();
        foreach (var item in metadata)
        {
            if (State.Invoices.TryGetValue(item.KsefNumber, out var existing))
            {
                existing.UpdateFrom(item);
                continue;
            }

            var invoice = new StoredInvoice
            {
                KsefNumber = item.KsefNumber,
                DiscoveredAtUtc = DateTimeOffset.UtcNow
            };
            invoice.UpdateFrom(item);
            State.Invoices[item.KsefNumber] = invoice;
            if (State.HistoricalBackfillBeforeIssueDate is { } cutoff && item.IssueDate < cutoff)
                invoice.ViewedAtUtc = DateTimeOffset.UtcNow;
            else
                added.Add(invoice);
        }
        return added;
    }

    private void PruneInvoicesOutsideVisibleRangeLocked()
    {
        var firstCurrentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var oldestVisible = firstCurrentMonth.AddMonths(-VisibleHistoryMonthsBack);
        var obsolete = State.Invoices
            .Where(x => x.Value.IssueDate != DateOnly.MinValue && x.Value.IssueDate < oldestVisible)
            .Select(x => x.Key)
            .ToList();
        foreach (var key in obsolete) State.Invoices.Remove(key);
    }

    private DateTimeOffset? ReserveInvoiceDownloadLocked()
    {
        var now = DateTimeOffset.UtcNow;
        if (State.InvoiceDownloadBlockedUntilUtc is { } blockedUntilUtc)
        {
            if (blockedUntilUtc > now) return blockedUntilUtc;
            State.InvoiceDownloadBlockedUntilUtc = null;
        }
        var windowStart = now.AddHours(-1);
        var futureLimit = now.AddMinutes(5);
        State.InvoiceDownloadAttemptsUtc.RemoveAll(x => x <= windowStart || x > futureLimit);
        if (State.InvoiceDownloadAttemptsUtc.Count >= MaxInvoiceDownloadsPerHour)
            return State.InvoiceDownloadAttemptsUtc.Min().AddHours(1).AddSeconds(1);

        State.InvoiceDownloadAttemptsUtc.Add(now);
        _store.SaveDownloadAttempts(State.InvoiceDownloadAttemptsUtc);
        return null;
    }

    private void RecordInvoiceDownloadBlock(int configurationVersion, TimeSpan? retryAfter)
    {
        if (configurationVersion < 0) return;
        var delay = retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero
            ? serverDelay
            : SyncInterval;
        var blockedUntilUtc = DateTimeOffset.UtcNow + delay;
        lock (_configurationGate)
        {
            if (configurationVersion != _configurationVersion) return;
            lock (_stateGate)
            {
                if (State.InvoiceDownloadBlockedUntilUtc is null ||
                    blockedUntilUtc > State.InvoiceDownloadBlockedUntilUtc)
                {
                    State.InvoiceDownloadBlockedUntilUtc = blockedUntilUtc;
                    _store.SaveState(State);
                }
            }
        }
    }

    private void RegisterOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) throw new OperationCanceledException("Aplikacja jest zamykana.", cancellationToken);
            _activeOperations++;
        }
    }

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

    private T WithCurrentConfiguration<T>(int configurationVersion, Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_configurationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configurationVersion != _configurationVersion)
                throw new OperationCanceledException("Konfiguracja KSeF zmieniła się podczas synchronizacji.", cancellationToken);
            lock (_stateGate) return action();
        }
    }

    private void WithCurrentConfiguration(int configurationVersion, Action action, CancellationToken cancellationToken) =>
        WithCurrentConfiguration(configurationVersion, () =>
        {
            action();
            return true;
        }, cancellationToken);

    private static TimeSpan MaxDelay(TimeSpan minimum, TimeSpan? candidate) =>
        candidate is { } delay && delay > minimum ? delay : minimum;

    private static DateTimeOffset GetInitialFromDate()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var nowWarsaw = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var firstCurrentMonth = new DateTime(nowWarsaw.Year, nowWarsaw.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var firstVisibleMonth = firstCurrentMonth.AddMonths(-VisibleHistoryMonthsBack);
        return new DateTimeOffset(firstVisibleMonth, zone.GetUtcOffset(firstVisibleMonth));
    }

    private void Schedule(TimeSpan due)
    {
        if (IsDisposed || _shutdownToken.IsCancellationRequested) return;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        lock (_scheduleGate)
        {
            _nextScheduledSyncUtc = DateTimeOffset.UtcNow + due;
            _timer?.Dispose();
            _timer = new Timer(TimerElapsed, null, due, Timeout.InfiniteTimeSpan);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelScheduledSync()
    {
        lock (_scheduleGate)
        {
            _nextScheduledSyncUtc = null;
            _timer?.Dispose();
            _timer = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string status, StatusSeverity severity = StatusSeverity.Information) =>
        StatusChanged?.Invoke(this, new AppStatusMessage(status, severity));

    private void ObserveBackgroundPersistence(Task persistence)
    {
        _ = persistence.ContinueWith(
            failed =>
            {
                var exception = failed.Exception?.GetBaseException() ?? new IOException("Nieznany błąd zapisu lokalnego stanu.");
                _log.Error("Pamięć lokalna", "Nie udało się zapisać lokalnego stanu aplikacji.", exception);
                SetStatus(UserFacingErrors.ForSynchronization(exception), StatusSeverity.Error);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        }
        _shutdown.Cancel();
        lock (_scheduleGate)
        {
            _timer?.Dispose();
            _timer = null;
            _nextScheduledSyncUtc = null;
        }
        lock (_configurationGate)
        {
            _configurationChanged.Cancel();
            if (!IsSynchronizing)
            {
                _client?.Dispose();
                _client = null;
            }
        }
        try
        {
            _store.FlushStateWrites();
        }
        catch (Exception exception)
        {
            _log.Error("Pamięć lokalna", "Nie udało się dokończyć zapisu przy zamykaniu aplikacji.", exception);
            SetStatus(UserFacingErrors.ForSynchronization(exception), StatusSeverity.Error);
        }
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
