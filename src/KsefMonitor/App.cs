using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Cykl życia WPF Application kończy zasoby w ExitApplication i OnExit.")]
internal sealed class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\KSeFMonitor.SingleInstance";
    private const string ActivationEventName = "Local\\KSeFMonitor.Activate";
    private AppStore? _store;
    private ApplicationLog? _log;
    private SynchronizationService? _synchronization;
    private MyDrSynchronizationService? _myDrSynchronization;
    private AppUpdateService? _updates;
    private MainWindow? _mainWindow;
    private PostUpdateInvocation? _postUpdateInvocation;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    [STAThread]
    public static int Main(string[] args)
    {
        if (UpdateInstaller.IsHelperInvocation(args)) return UpdateInstaller.RunHelper(args);
        PostUpdateInvocation? postUpdateInvocation;
        try
        {
            _ = UpdateInstaller.TryParsePostUpdateInvocation(args, out postUpdateInvocation);
        }
        catch (AppUpdateException exception)
        {
            System.Windows.MessageBox.Show(
                exception.UserMessage,
                "KSeF Monitor — aktualizacja",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 33;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        if (!createdNew)
        {
            activationEvent.Set();
            return 0;
        }

        var app = new App
        {
            _activationEvent = activationEvent,
            _postUpdateInvocation = postUpdateInvocation
        };
        app.DispatcherUnhandledException += app.OnDispatcherUnhandledException;
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppPaths.EnsureCreated();

        var polishCulture = CultureInfo.GetCultureInfo("pl-PL");
        CultureInfo.DefaultThreadCurrentCulture = polishCulture;
        CultureInfo.DefaultThreadCurrentUICulture = polishCulture;

        Resources[SystemFonts.MessageFontFamilyKey] = new FontFamily("Segoe UI");
        Resources[SystemFonts.MessageFontSizeKey] = 14d;

        _log = new ApplicationLog(AppPaths.LogFile);
        UpdateInstaller.CleanupStaleArtifacts(_log);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _log.Info("Aplikacja", $"Uruchomiono KSeF Monitor v{version?.Major ?? 0}.{version?.Minor ?? 0}.{Math.Max(0, version?.Build ?? 0)} na {Environment.OSVersion}.");
        _store = new AppStore(_log);
        _synchronization = new SynchronizationService(_store);
        _myDrSynchronization = new MyDrSynchronizationService(_store);
        _updates = new AppUpdateService(_log);
        _mainWindow = new MainWindow(_store, _synchronization, _myDrSynchronization, _updates);
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent!,
            OnActivationRequested,
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        if (UpdateInstaller.ConsumeFailureMarker(_log) is { } updateFailure)
            _mainWindow.ShowStatusMessage(new AppStatusMessage(updateFailure, StatusSeverity.Error));
        Dispatcher.BeginInvoke(
            () => _ = CompleteUpdateStartupHandshakeAsync(_postUpdateInvocation),
            DispatcherPriority.ApplicationIdle);
    }

    public void ExitApplication()
    {
        StopActivationListener();
        _mainWindow?.PrepareForExit();
        _myDrSynchronization?.Dispose();
        _synchronization?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopActivationListener();
        _myDrSynchronization?.Dispose();
        _synchronization?.Dispose();
        _updates?.Dispose();
        _log?.Info("Aplikacja", "Zakończono działanie aplikacji.");
        base.OnExit(e);
    }

    private void OnActivationRequested(object? state, bool timedOut)
    {
        if (timedOut || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() => _mainWindow?.ShowFromTray(), DispatcherPriority.Send);
    }

    private void StopActivationListener()
    {
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent = null;
    }

    private async Task CompleteUpdateStartupHandshakeAsync(PostUpdateInvocation? invocation)
    {
        var log = _log;
        if (log is null) return;
        await Task.Run(() =>
        {
            if (invocation is not null) UpdateInstaller.SignalPostUpdateHealth(invocation, log);
            UpdateInstaller.RecoverInterruptedSessionsAfterStartup(log);
        }).ConfigureAwait(false);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Aplikacja", "Nieobsłużony błąd interfejsu.", e.Exception);
        System.Windows.MessageBox.Show(
            UserFacingErrors.ForUnexpectedError(),
            "KSeF Monitor — błąd",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
