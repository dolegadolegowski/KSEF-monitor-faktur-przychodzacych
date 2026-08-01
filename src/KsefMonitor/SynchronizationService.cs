using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class SynchronizationService : IDisposable
{
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromMinutes(5);
    private const int MaxInvoiceDownloadsPerHour = 60;

    private readonly AppStore _store;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateGate = new();
    private Timer? _timer;
    private KsefApiClient? _client;
    private AppSettings _settings;
    private string? _token;
    private bool _disposed;

    public SynchronizationService(AppStore store)
    {
        _store = store;
        _settings = store.LoadSettings();
        _token = store.LoadToken();
        State = store.LoadState();
    }

    public AppState State { get; }
    public bool IsSynchronizing { get; private set; }
    public DateTimeOffset? NextScheduledSyncUtc { get; private set; }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? StateChanged;
    public event EventHandler<IReadOnlyList<StoredInvoice>>? NewInvoicesDiscovered;

    public void Start()
    {
        if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(_token))
        {
            SetStatus("Skonfiguruj NIP i token KSeF.");
            return;
        }

        var sinceLast = State.LastSuccessfulSyncUtc is { } last
            ? DateTimeOffset.UtcNow - last
            : SyncInterval;
        var due = sinceLast >= SyncInterval ? TimeSpan.Zero : SyncInterval - sinceLast;
        Schedule(due);
    }

    public void UpdateConfiguration()
    {
        _settings = _store.LoadSettings();
        _token = _store.LoadToken();
        _client?.Dispose();
        _client = null;
        Start();
    }

    public async Task RefreshNowAsync()
    {
        if (State.LastMetadataSyncAttemptUtc is { } lastAttempt)
        {
            var remaining = ManualRefreshCooldown - (DateTimeOffset.UtcNow - lastAttempt);
            if (remaining > TimeSpan.Zero)
                throw new InvalidOperationException($"Kolejne ręczne odświeżenie będzie możliwe za {Math.Ceiling(remaining.TotalMinutes):0} min. Chroni to limity API KSeF.");
        }
        await SynchronizeAsync(manual: true, _shutdown.Token).ConfigureAwait(false);
    }

    public void MarkViewed(string ksefNumber)
    {
        lock (_stateGate)
        {
            if (!State.Invoices.TryGetValue(ksefNumber, out var invoice)) return;
            invoice.ViewedAtUtc ??= DateTimeOffset.UtcNow;
            _store.SaveState(State);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkNotified(IEnumerable<string> ksefNumbers)
    {
        lock (_stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var number in ksefNumbers)
                if (State.Invoices.TryGetValue(number, out var invoice)) invoice.NotifiedAtUtc ??= now;
            _store.SaveState(State);
        }
    }

    private async void TimerElapsed(object? state)
    {
        try
        {
            await SynchronizeAsync(manual: false, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Zamykanie aplikacji.
        }
    }

    private async Task SynchronizeAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (manual) SetStatus("Synchronizacja już trwa.");
            return;
        }

        IsSynchronizing = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            _settings = _store.LoadSettings();
            _token = _store.LoadToken();
            if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException("Brak poprawnego NIP-u lub tokena KSeF.");

            _client ??= new KsefApiClient(_settings, _token);
            SetStatus("Łączenie z KSeF…");

            var from = State.PermanentStorageHwmDate?.AddSeconds(-1) ?? GetInitialFromDate();
            State.LastMetadataSyncAttemptUtc = DateTimeOffset.UtcNow;
            lock (_stateGate) _store.SaveState(State);
            var result = await _client.QueryReceivedInvoicesAsync(from, cancellationToken).ConfigureAwait(false);
            var newInvoices = MergeMetadata(result.Invoices);
            PruneInvoicesOutsideVisibleRange();

            if (result.PermanentStorageHwmDate is { } hwm &&
                (State.PermanentStorageHwmDate is null || hwm > State.PermanentStorageHwmDate))
                State.PermanentStorageHwmDate = hwm;

            // Najpierw utrwalamy metadane i punkt HWM. Pobieranie XML nie może cofnąć poprawnej synchronizacji listy.
            lock (_stateGate) _store.SaveState(State);
            StateChanged?.Invoke(this, EventArgs.Empty);

            var downloadWindowStart = DateTimeOffset.UtcNow.AddHours(-1);
            State.InvoiceDownloadAttemptsUtc.RemoveAll(x => x <= downloadWindowStart);
            var availableDownloads = Math.Max(0, MaxInvoiceDownloadsPerHour - State.InvoiceDownloadAttemptsUtc.Count);
            var pendingXml = State.Invoices.Values
                .Where(x => string.IsNullOrWhiteSpace(x.Xml))
                .OrderByDescending(x => x.IsNew)
                .ThenBy(x => x.DiscoveredAtUtc)
                .Take(availableDownloads)
                .ToList();

            for (var index = 0; index < pendingXml.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > 0) await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
                var invoice = pendingXml[index];
                SetStatus($"Pobieranie treści faktur: {index + 1}/{pendingXml.Count}…");
                State.InvoiceDownloadAttemptsUtc.Add(DateTimeOffset.UtcNow);
                lock (_stateGate) _store.SaveState(State);
                try
                {
                    invoice.Xml = await _client.DownloadInvoiceXmlAsync(invoice.KsefNumber, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (KsefApiException)
                {
                    // Metadane pozostają dostępne. XML zostanie ponowiony w następnym cyklu.
                }
            }

            State.LastSuccessfulSyncUtc = DateTimeOffset.UtcNow;
            lock (_stateGate) _store.SaveState(State);
            SetStatus(newInvoices.Count == 0
                ? $"Brak nowych faktur. Odświeżono {DateTime.Now:HH:mm}."
                : $"Nowe faktury: {newInvoices.Count}. Odświeżono {DateTime.Now:HH:mm}.");
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (newInvoices.Count > 0) NewInvoicesDiscovered?.Invoke(this, newInvoices);
            Schedule(SyncInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Synchronizacja zatrzymana.");
        }
        catch (Exception exception)
        {
            SetStatus($"Błąd synchronizacji: {exception.Message}");
            Schedule(FailureRetryInterval);
            if (manual) throw;
        }
        finally
        {
            IsSynchronizing = false;
            _syncGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<StoredInvoice> MergeMetadata(IEnumerable<InvoiceMetadata> metadata)
    {
        var added = new List<StoredInvoice>();
        lock (_stateGate)
        {
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
                added.Add(invoice);
            }
        }
        return added;
    }

    private void PruneInvoicesOutsideVisibleRange()
    {
        var firstCurrentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var oldestVisible = firstCurrentMonth.AddMonths(-1);
        var obsolete = State.Invoices
            .Where(x => x.Value.IssueDate != DateOnly.MinValue && x.Value.IssueDate < oldestVisible)
            .Select(x => x.Key)
            .ToList();
        foreach (var key in obsolete) State.Invoices.Remove(key);
    }

    private static DateTimeOffset GetInitialFromDate()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        var nowWarsaw = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var firstCurrentMonth = new DateTime(nowWarsaw.Year, nowWarsaw.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var firstPreviousMonth = firstCurrentMonth.AddMonths(-1);
        return new DateTimeOffset(firstPreviousMonth, zone.GetUtcOffset(firstPreviousMonth));
    }

    private void Schedule(TimeSpan due)
    {
        if (_disposed || _shutdown.IsCancellationRequested) return;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        NextScheduledSyncUtc = DateTimeOffset.UtcNow + due;
        _timer?.Dispose();
        _timer = new Timer(TimerElapsed, null, due, Timeout.InfiniteTimeSpan);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _timer?.Dispose();
        _client?.Dispose();
        _syncGate.Dispose();
        _shutdown.Dispose();
    }
}
