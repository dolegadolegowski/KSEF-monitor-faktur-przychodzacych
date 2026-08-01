using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KsefMonitor;

internal sealed class App : System.Windows.Application
{
    private AppStore? _store;
    private SynchronizationService? _synchronization;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstance;

    [STAThread]
    public static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Local\\KSeFMonitor.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "KSeF Monitor jest już uruchomiony. Sprawdź ikonę w obszarze powiadomień.",
                "KSeF Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return 0;
        }

        var app = new App { _singleInstance = mutex };
        app.DispatcherUnhandledException += app.OnDispatcherUnhandledException;
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppPaths.EnsureCreated();

        Resources[SystemFonts.MessageFontFamilyKey] = new FontFamily("Segoe UI");
        Resources[SystemFonts.MessageFontSizeKey] = 14d;

        _store = new AppStore();
        _synchronization = new SynchronizationService(_store);
        _mainWindow = new MainWindow(_store, _synchronization);
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    public void ExitApplication()
    {
        _mainWindow?.PrepareForExit();
        _synchronization?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _synchronization?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"Wystąpił nieoczekiwany błąd:\n\n{e.Exception.Message}",
            "KSeF Monitor — błąd",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
