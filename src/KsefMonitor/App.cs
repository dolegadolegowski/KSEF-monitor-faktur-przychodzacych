using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Threading;
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
    private MainWindow? _mainWindow;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    [STAThread]
    public static int Main()
    {
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
            _singleInstance = mutex,
            _activationEvent = activationEvent
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
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _log.Info("Aplikacja", $"Uruchomiono KSeF Monitor v{version?.Major ?? 0}.{version?.Minor ?? 0}.{Math.Max(0, version?.Build ?? 0)} na {Environment.OSVersion}.");
        _store = new AppStore(_log);
        _synchronization = new SynchronizationService(_store);
        _mainWindow = new MainWindow(_store, _synchronization);
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent!,
            OnActivationRequested,
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void ExitApplication()
    {
        StopActivationListener();
        _mainWindow?.PrepareForExit();
        _synchronization?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopActivationListener();
        _synchronization?.Dispose();
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
