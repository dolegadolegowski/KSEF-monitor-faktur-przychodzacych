using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operacje użytkownika pokazują każdy błąd połączenia lub zapisu w oknie ustawień.")]
internal sealed class SettingsWindow : Window
{
    private readonly AppStore _store;
    private readonly SynchronizationService _synchronization;
    private readonly MyDrSynchronizationService _myDr;
    private readonly AppUpdateService _updates;
    private readonly TextBox _nip = new();
    private readonly PasswordBox _token = new();
    private readonly CheckBox _notifications = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _logText = new();
    private readonly TextBlock _logStatus = new();
    private readonly Button _testButton = new();
    private readonly Button _saveButton = new();
    private readonly TextBox _myDrClientId = new();
    private readonly PasswordBox _myDrClientSecret = new();
    private readonly PasswordBox _myDrRefreshToken = new();
    private readonly TextBlock _myDrActionStatus = new();
    private readonly TextBlock _myDrCredentialsStatus = new();
    private readonly TextBlock _myDrLastAttempt = new();
    private readonly TextBlock _myDrLastSuccess = new();
    private readonly TextBlock _myDrNextAttempt = new();
    private readonly TextBlock _myDrLastError = new();
    private readonly Button _myDrSaveButton = new();
    private readonly Button _myDrRefreshButton = new();
    private readonly Button _myDrDeleteButton = new();
    private readonly TextBlock _updateStatus = new();
    private readonly TextBlock _updateVersion = new();
    private readonly Button _updateCheckButton = new();
    private readonly Button _updateInstallButton = new();
    private readonly Button _updateReleasePageButton = new();
    private readonly DispatcherTimer _myDrStatusTimer;
    private TabItem? _logTab;
    private bool _requiresProductionToken;
    private string _originalNip = string.Empty;
    private Guid _savedMyDrConnectionId;
    private CancellationTokenSource? _testCancellation;
    private CancellationTokenSource? _myDrCancellation;
    private bool _myDrStatusReadFailed;
    private bool _isClosing;

    public SettingsWindow(
        AppStore store,
        SynchronizationService synchronization,
        MyDrSynchronizationService myDr,
        AppUpdateService updates)
    {
        _store = store;
        _synchronization = synchronization;
        _myDr = myDr;
        _updates = updates;
        Title = "Ustawienia aplikacji";
        Width = 700;
        Height = 620;
        MinWidth = 620;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Icon = TrayIconFactory.CreateImageSource();
        Content = BuildContent();
        LoadCurrentSettings();
        LoadMyDrSettings();
        _myDrStatusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _myDrStatusTimer.Tick += (_, _) => RefreshMyDrStatus();
        _myDrStatusTimer.Start();
        _updates.StateChanged += OnUpdateStateChanged;
        Loaded += async (_, _) => await CheckUpdatesOnOpenAsync().ConfigureAwait(true);
        Closing += (_, _) =>
        {
            _isClosing = true;
            _testCancellation?.Cancel();
            _myDrCancellation?.Cancel();
            _myDrStatusTimer.Stop();
            _updates.StateChanged -= OnUpdateStateChanged;
        };
    }

    public bool ConfigurationChanged { get; private set; }

    private TabControl BuildContent()
    {
        var tabs = new TabControl { Margin = new Thickness(14) };
        var ksefTab = new TabItem { Header = "KSeF", Content = BuildConnectionContent() };
        var myDrTab = new TabItem { Header = "MyDR", Content = BuildMyDrContent() };
        tabs.Items.Add(ksefTab);
        tabs.Items.Add(myDrTab);
        var updatesTab = new TabItem { Header = "Aktualizacje", Content = BuildUpdatesContent() };
        tabs.Items.Add(updatesTab);
        _logTab = new TabItem { Header = "Dziennik", Content = BuildLogContent() };
        tabs.Items.Add(_logTab);
        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, tabs)) return;
            _saveButton.IsDefault = ReferenceEquals(tabs.SelectedItem, ksefTab);
            _myDrSaveButton.IsDefault = ReferenceEquals(tabs.SelectedItem, myDrTab);
            if (ReferenceEquals(tabs.SelectedItem, updatesTab)) ApplyUpdateStatus(_updates.GetSnapshot());
            if (ReferenceEquals(tabs.SelectedItem, _logTab)) RefreshLog();
        };
        return tabs;
    }

    private Grid BuildConnectionContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "Połączenie z KSeF",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Połączenie produkcyjne • token powinien mieć wyłącznie uprawnienie InvoiceRead.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 20)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabel(form, "NIP firmy", 0);
        _nip.Margin = new Thickness(0, 0, 0, 14);
        _nip.MaxLength = 13;
        Grid.SetRow(_nip, 0);
        Grid.SetColumn(_nip, 1);
        form.Children.Add(_nip);

        AddLabel(form, "Token KSeF", 1);
        _token.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(_token, 1);
        Grid.SetColumn(_token, 1);
        form.Children.Add(_token);

        var tokenHint = new TextBlock
        {
            Text = "Pozostaw puste, aby zachować już zapisany token. Sekret jest chroniony przez Windows DPAPI.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(tokenHint, 2);
        Grid.SetColumn(tokenHint, 1);
        form.Children.Add(tokenHint);

        _notifications.Content = "Pokazuj powiadomienia o nowych fakturach";
        _notifications.Margin = new Thickness(0, 0, 0, 18);
        Grid.SetRow(_notifications, 3);
        Grid.SetColumn(_notifications, 1);
        form.Children.Add(_notifications);

        var warning = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 247, 224)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(232, 181, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "Aplikacja łączy się wyłącznie z produkcyjnym KSeF. Wprowadź rzeczywisty NIP firmy i token wygenerowany w środowisku produkcyjnym.",
                TextWrapping = TextWrapping.Wrap
            }
        };
        Grid.SetRow(warning, 4);
        Grid.SetColumn(warning, 0);
        Grid.SetColumnSpan(warning, 2);
        form.Children.Add(warning);

        _status.Margin = new Thickness(0, 14, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 5);
        Grid.SetColumn(_status, 0);
        Grid.SetColumnSpan(_status, 2);
        form.Children.Add(_status);

        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _testButton.Content = "Sprawdź połączenie";
        _testButton.Padding = new Thickness(14, 7, 14, 7);
        _testButton.Margin = new Thickness(0, 0, 10, 0);
        _testButton.Click += async (_, _) => await TestConnectionAsync().ConfigureAwait(true);
        actions.Children.Add(_testButton);

        var cancel = new Button { Content = "Anuluj", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close();
        actions.Children.Add(cancel);

        _saveButton.Content = "Zapisz";
        _saveButton.Padding = new Thickness(18, 7, 18, 7);
        _saveButton.IsDefault = true;
        _saveButton.Click += (_, _) => SaveAndClose();
        actions.Children.Add(_saveButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        return root;
    }

    private ScrollViewer BuildMyDrContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock
        {
            Text = "Połączenie z MyDR",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Dane służą wyłącznie do dziennego pobierania obrotu z wykonanych prywatnych usług medycznych.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabel(form, "Client ID", 0);
        _myDrClientId.Margin = new Thickness(0, 0, 0, 14);
        _myDrClientId.MaxLength = 500;
        Grid.SetRow(_myDrClientId, 0);
        Grid.SetColumn(_myDrClientId, 1);
        form.Children.Add(_myDrClientId);

        AddLabel(form, "Client secret", 1);
        _myDrClientSecret.Margin = new Thickness(0, 0, 0, 14);
        _myDrClientSecret.MaxLength = 16_384;
        Grid.SetRow(_myDrClientSecret, 1);
        Grid.SetColumn(_myDrClientSecret, 1);
        form.Children.Add(_myDrClientSecret);

        AddLabel(form, "Refresh token", 2);
        _myDrRefreshToken.Margin = new Thickness(0, 0, 0, 5);
        _myDrRefreshToken.MaxLength = 16_384;
        Grid.SetRow(_myDrRefreshToken, 2);
        Grid.SetColumn(_myDrRefreshToken, 1);
        form.Children.Add(_myDrRefreshToken);

        var secretHint = new TextBlock
        {
            Text = "Zapisane sekrety nigdy nie są wyświetlane. Pozostaw pola Client secret i Refresh token puste, aby zachować dotychczasowe wartości. Dane są chronione przez Windows DPAPI.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(secretHint, 3);
        Grid.SetColumn(secretHint, 1);
        form.Children.Add(secretHint);

        _myDrActionStatus.TextWrapping = TextWrapping.Wrap;
        _myDrActionStatus.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_myDrActionStatus, 4);
        Grid.SetColumn(_myDrActionStatus, 0);
        Grid.SetColumnSpan(_myDrActionStatus, 2);
        form.Children.Add(_myDrActionStatus);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _myDrSaveButton.Content = "Zapisz dane MyDR";
        _myDrSaveButton.Padding = new Thickness(14, 7, 14, 7);
        _myDrSaveButton.Margin = new Thickness(0, 0, 10, 8);
        _myDrSaveButton.Click += (_, _) => SaveMyDrSettings();
        actions.Children.Add(_myDrSaveButton);

        _myDrRefreshButton.Content = "Odśwież teraz";
        _myDrRefreshButton.ToolTip = "Pełne przeliczenie czterech miesięcy. Może wykonać po jednym zapytaniu o usługi dla każdej rozliczonej wizyty.";
        _myDrRefreshButton.Padding = new Thickness(14, 7, 14, 7);
        _myDrRefreshButton.Margin = new Thickness(0, 0, 10, 8);
        _myDrRefreshButton.Click += async (_, _) => await RefreshMyDrNowAsync().ConfigureAwait(true);
        actions.Children.Add(_myDrRefreshButton);

        _myDrDeleteButton.Content = "Usuń dane MyDR";
        _myDrDeleteButton.Padding = new Thickness(14, 7, 14, 7);
        _myDrDeleteButton.Margin = new Thickness(0, 0, 0, 8);
        _myDrDeleteButton.Click += (_, _) => DeleteMyDrSettings();
        actions.Children.Add(_myDrDeleteButton);
        Grid.SetRow(actions, 5);
        Grid.SetColumn(actions, 0);
        Grid.SetColumnSpan(actions, 2);
        form.Children.Add(actions);

        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(218, 223, 230)),
            Margin = new Thickness(0, 7, 0, 17)
        };
        Grid.SetRow(separator, 2);
        root.Children.Add(separator);

        var statusPanel = new Grid();
        statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        statusPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++) statusPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddStatusRow(statusPanel, "Dane dostępowe", _myDrCredentialsStatus, 0);
        AddStatusRow(statusPanel, "Ostatnia próba", _myDrLastAttempt, 1);
        AddStatusRow(statusPanel, "Ostatni sukces", _myDrLastSuccess, 2);
        AddStatusRow(statusPanel, "Kolejna próba", _myDrNextAttempt, 3);
        AddStatusRow(statusPanel, "Ostatni błąd", _myDrLastError, 4);
        Grid.SetRow(statusPanel, 3);
        root.Children.Add(statusPanel);

        return new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
    }

    private Grid BuildUpdatesContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        heading.Children.Add(new TextBlock
        {
            Text = "Aktualizacje aplikacji",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Aplikacja sprawdza publiczne wydania w oficjalnym repozytorium GitHub przy każdym uruchomieniu i po otwarciu ustawień.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        root.Children.Add(heading);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var versionRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionRow.Children.Add(new TextBlock
        {
            Text = "Zainstalowana wersja",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _updateVersion.Text = ProductInformation.DisplayVersion;
        _updateVersion.FontSize = 16;
        _updateVersion.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_updateVersion, 1);
        versionRow.Children.Add(_updateVersion);
        panel.Children.Add(versionRow);

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 223, 230)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14)
        };
        _updateStatus.TextWrapping = TextWrapping.Wrap;
        _updateStatus.Text = "Jeszcze nie sprawdzono aktualizacji.";
        statusBorder.Child = _updateStatus;
        panel.Children.Add(statusBorder);

        panel.Children.Add(new TextBlock
        {
            Text = "Przed instalacją aplikacja porównuje wersję, rozmiar i dwie sumy SHA-256. Następnie tworzy kopię poprzedniego EXE i automatycznie ją przywraca, jeśli nowa wersja nie uruchomi się poprawnie.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 16, 0, 0)
        });
        Grid.SetRow(panel, 1);
        root.Children.Add(panel);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _updateCheckButton.Content = "Sprawdź teraz";
        _updateCheckButton.Padding = new Thickness(14, 7, 14, 7);
        _updateCheckButton.Margin = new Thickness(0, 0, 10, 0);
        _updateCheckButton.Click += async (_, _) => await CheckUpdatesManuallyAsync().ConfigureAwait(true);
        actions.Children.Add(_updateCheckButton);

        _updateReleasePageButton.Content = "Strona wydania";
        _updateReleasePageButton.Padding = new Thickness(14, 7, 14, 7);
        _updateReleasePageButton.Margin = new Thickness(0, 0, 10, 0);
        _updateReleasePageButton.Visibility = Visibility.Collapsed;
        _updateReleasePageButton.Click += (_, _) => OpenReleasePage();
        actions.Children.Add(_updateReleasePageButton);

        _updateInstallButton.Content = "Zainstaluj aktualizację";
        _updateInstallButton.Padding = new Thickness(14, 7, 14, 7);
        _updateInstallButton.Visibility = Visibility.Collapsed;
        _updateInstallButton.Click += async (_, _) => await InstallUpdateFromSettingsAsync().ConfigureAwait(true);
        actions.Children.Add(_updateInstallButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        ApplyUpdateStatus(_updates.GetSnapshot());
        return root;
    }

    private async Task CheckUpdatesOnOpenAsync()
    {
        try
        {
            await _updates.CheckForUpdatesAsync(force: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _store.Log.Warning("Aktualizacja", "Nieoczekiwany błąd sprawdzania po otwarciu ustawień.", exception);
        }
    }

    private async Task CheckUpdatesManuallyAsync()
    {
        _updateCheckButton.IsEnabled = false;
        try
        {
            await _updates.CheckForUpdatesAsync(force: true).ConfigureAwait(true);
        }
        finally
        {
            if (!_isClosing) ApplyUpdateStatus(_updates.GetSnapshot());
        }
    }

    private async Task InstallUpdateFromSettingsAsync()
    {
        var snapshot = _updates.GetSnapshot();
        if (snapshot.AvailableRelease is not { } release) return;
        if (System.Windows.MessageBox.Show(
                $"Zainstalować KSeF Monitor v{release.Version}?\n\nAplikacja zamknie się i uruchomi ponownie.",
                "Aktualizacja KSeF Monitor",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        try
        {
            await _updates.PrepareAndLaunchUpdateAsync().ConfigureAwait(true);
            ((App)System.Windows.Application.Current).ExitApplication();
        }
        catch (AppUpdateException exception)
        {
            if (!_isClosing)
                System.Windows.MessageBox.Show(
                    exception.UserMessage,
                    "Nie udało się zainstalować aktualizacji",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            else if (Owner is MainWindow mainWindow)
                mainWindow.ShowStatusMessage(new AppStatusMessage(exception.UserMessage, StatusSeverity.Error));
        }
        catch (Exception exception)
        {
            _store.Log.Error("Aktualizacja", "Nieoczekiwany błąd instalowania aktualizacji w ustawieniach.", exception);
            const string message = "Nie udało się zainstalować aktualizacji. Aplikacja nie została zmieniona.";
            if (!_isClosing)
                System.Windows.MessageBox.Show(
                    message,
                    "Nie udało się zainstalować aktualizacji",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            else if (Owner is MainWindow mainWindow)
                mainWindow.ShowStatusMessage(new AppStatusMessage(message, StatusSeverity.Error));
        }
        finally
        {
            if (!_isClosing) ApplyUpdateStatus(_updates.GetSnapshot());
        }
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosing && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                ApplyUpdateStatus(_updates.GetSnapshot());
        });
    }

    private void ApplyUpdateStatus(AppUpdateSnapshot snapshot)
    {
        _updateStatus.Text = snapshot.Message ?? snapshot.Phase switch
        {
            AppUpdatePhase.Idle => "Jeszcze nie sprawdzono aktualizacji.",
            AppUpdatePhase.Checking => "Sprawdzanie aktualizacji…",
            _ => "Stan aktualizacji jest nieznany."
        };
        _updateStatus.Foreground = snapshot.HasError ? Brushes.Firebrick : Brushes.Black;
        _updateStatus.FontWeight = snapshot.HasAvailableUpdate ? FontWeights.SemiBold : FontWeights.Normal;
        _updateCheckButton.IsEnabled = snapshot.Phase is not (AppUpdatePhase.Checking or AppUpdatePhase.Downloading or AppUpdatePhase.Preparing or AppUpdatePhase.ReadyToRestart);
        _updateInstallButton.Visibility = snapshot.HasAvailableUpdate ? Visibility.Visible : Visibility.Collapsed;
        _updateReleasePageButton.Visibility = snapshot.AvailableRelease is not null ? Visibility.Visible : Visibility.Collapsed;
        _updateInstallButton.IsEnabled = snapshot.Phase is AppUpdatePhase.Available or AppUpdatePhase.Failed;
        _updateInstallButton.Content = snapshot.Phase switch
        {
            AppUpdatePhase.Downloading when snapshot.ProgressPercent is { } percent => $"Pobieranie {percent}%…",
            AppUpdatePhase.Preparing => "Przygotowywanie…",
            AppUpdatePhase.ReadyToRestart => "Ponowne uruchamianie…",
            _ => "Zainstaluj aktualizację"
        };
    }

    private void OpenReleasePage()
    {
        var uri = _updates.GetSnapshot().AvailableRelease?.ReleasePageUri;
        if (uri is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _store.Log.Warning("Aktualizacja", "Nie udało się otworzyć strony wydania.", exception);
            System.Windows.MessageBox.Show("Nie udało się otworzyć strony wydania w przeglądarce.", "KSeF Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Grid BuildLogContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = "Dziennik aplikacji",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Zawiera informacje techniczne pomocne przy diagnozowaniu problemów. Dziennik nie przechowuje tokenów ani innych sekretów KSeF lub MyDR ani treści XML faktur.",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        root.Children.Add(heading);

        _logText.IsReadOnly = true;
        _logText.AcceptsReturn = true;
        _logText.AcceptsTab = true;
        _logText.TextWrapping = TextWrapping.NoWrap;
        _logText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logText.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logText.FontFamily = new FontFamily("Consolas");
        _logText.FontSize = 11.5;
        _logText.Background = Brushes.White;
        _logText.Padding = new Thickness(8);
        Grid.SetRow(_logText, 1);
        root.Children.Add(_logText);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _logStatus.Foreground = Brushes.DimGray;
        _logStatus.VerticalAlignment = VerticalAlignment.Center;
        _logStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_logStatus);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Odśwież", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        refresh.Click += (_, _) => RefreshLog();
        actions.Children.Add(refresh);
        var copy = new Button { Content = "Kopiuj", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 10, 0) };
        copy.Click += (_, _) => CopyLog();
        actions.Children.Add(copy);
        var close = new Button { Content = "Zamknij", Padding = new Thickness(14, 7, 14, 7) };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void RefreshLog()
    {
        _logText.Text = _store.Log.ReadRecent();
        _logText.ScrollToEnd();
        _logStatus.Text = $"Plik: {_store.Log.FilePath}";
        _logStatus.ToolTip = _store.Log.FilePath;
    }

    private void CopyLog()
    {
        try
        {
            System.Windows.Clipboard.SetText(_logText.Text);
            _logStatus.Text = "Skopiowano dziennik do schowka.";
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia", "Nie udało się skopiować dziennika do schowka.", exception);
            _logStatus.Text = "Nie udało się skopiować dziennika.";
        }
    }

    private void LoadCurrentSettings()
    {
        var settings = _store.LoadSettings();
        _requiresProductionToken = settings.RequiresProductionToken;
        _originalNip = NipValidator.Normalize(settings.Nip);
        _nip.Text = settings.Nip;
        _notifications.IsChecked = settings.NotificationsEnabled;

        if (_requiresProductionToken)
            SetStatus("Poprzedni token pochodził z TEST/DEMO. Wprowadź nowy token produkcyjny.", isError: true);
    }

    private void LoadMyDrSettings()
    {
        try
        {
            var credentials = _store.LoadMyDrCredentials();
            _savedMyDrConnectionId = credentials is not null && credentials.IsConfigured
                ? credentials.ConnectionId
                : Guid.Empty;
            _myDrClientId.Text = credentials is not null && credentials.IsConfigured
                ? credentials.ClientId
                : string.Empty;
            _myDrClientSecret.Password = string.Empty;
            _myDrRefreshToken.Password = string.Empty;
            RefreshMyDrStatus();
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia MyDR", "Nie udało się odczytać ustawień MyDR.", exception);
            SetMyDrActionStatus("Nie udało się odczytać zapisanych danych MyDR. Możesz wprowadzić je ponownie.", isError: true);
            RefreshMyDrStatus();
        }
    }

    private void SaveMyDrSettings()
    {
        try
        {
            if (!TrySaveMyDrSettings(restartScheduler: true, out var changed)) return;
            SetMyDrActionStatus(changed ? "Dane dostępowe MyDR zostały zapisane." : "Dane dostępowe MyDR są aktualne.", isError: false);
            RefreshMyDrStatus();
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia MyDR", "Nie udało się zapisać danych dostępowych MyDR.", exception);
            SetMyDrActionStatus("Nie udało się zapisać danych MyDR. Spróbuj ponownie.", isError: true);
        }
    }

    private bool TrySaveMyDrSettings(bool restartScheduler, out bool changed)
    {
        changed = false;
        var clientId = _myDrClientId.Text.Trim();
        var enteredClientSecret = _myDrClientSecret.Password;
        var enteredRefreshToken = _myDrRefreshToken.Password;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            SetMyDrActionStatus("Wprowadź Client ID, Client secret i Refresh token. Wszystkie trzy dane są potrzebne do połączenia z MyDR.", isError: true);
            return false;
        }

        string? validationError = null;
        MyDrCredentials? savedResult = null;
        var applied = _myDr.TryApplyConfigurationChange(() =>
        {
            // Odczyt i zapis odbywają się pod tą samą blokadą co synchronizacja.
            // Dzięki temu rotacja Refresh Tokena nie może zostać nadpisana
            // wartością odczytaną chwilę wcześniej przez okno ustawień.
            var latestCredentials = _store.LoadMyDrCredentials();
            var savedCredentials = latestCredentials is not null && latestCredentials.IsConfigured
                ? latestCredentials
                : null;
            var clientSecret = string.IsNullOrWhiteSpace(enteredClientSecret)
                ? savedCredentials?.ClientSecret ?? string.Empty
                : enteredClientSecret;
            var refreshToken = string.IsNullOrWhiteSpace(enteredRefreshToken)
                ? savedCredentials?.RefreshToken ?? string.Empty
                : enteredRefreshToken;

            if (string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(refreshToken))
            {
                validationError = "Wprowadź Client ID, Client secret i Refresh token. Wszystkie trzy dane są potrzebne do połączenia z MyDR.";
                return false;
            }

            var configurationChanged = savedCredentials is null
                                       || !string.Equals(clientId, savedCredentials.ClientId, StringComparison.Ordinal)
                                       || !string.Equals(clientSecret, savedCredentials.ClientSecret, StringComparison.Ordinal)
                                       || !string.Equals(refreshToken, savedCredentials.RefreshToken, StringComparison.Ordinal);
            if (!configurationChanged)
            {
                savedResult = savedCredentials;
                return false;
            }

            var credentials = new MyDrCredentials
            {
                ConnectionId = Guid.NewGuid(),
                ClientId = clientId,
                ClientSecret = clientSecret,
                RefreshToken = refreshToken
            };
            _store.SaveMyDrCredentials(credentials);
            savedResult = credentials;
            _store.Log.Info("Ustawienia MyDR", "Zapisano dane dostępowe MyDR.");
            return true;
        }, restartScheduler, out var configurationChanged);

        if (!applied)
        {
            SetMyDrActionStatus("Trwa synchronizacja MyDR. Spróbuj ponownie po jej zakończeniu.", isError: false);
            return false;
        }

        if (validationError is not null)
        {
            if (!restartScheduler) _myDr.Start();
            SetMyDrActionStatus(validationError, isError: true);
            return false;
        }

        changed = configurationChanged;
        _savedMyDrConnectionId = savedResult?.ConnectionId ?? Guid.Empty;
        _myDrClientId.Text = savedResult?.ClientId ?? clientId;
        _myDrClientSecret.Password = string.Empty;
        _myDrRefreshToken.Password = string.Empty;
        return true;
    }

    private async Task RefreshMyDrNowAsync()
    {
        CancellationTokenSource? timeout = null;
        try
        {
            if (!TrySaveMyDrSettings(restartScheduler: false, out var changed)) return;
            if (changed) SetMyDrActionStatus("Dane zapisano. Pobieranie obrotu z MyDR…", isError: false);
            else SetMyDrActionStatus("Pobieranie obrotu z MyDR…", isError: false);

            SetMyDrBusy(true);
            // Pierwsze przeliczenie może objąć cztery miesiące. Klient celowo
            // rozkłada żądania w czasie, dlatego nie przerywamy bezpiecznego
            // pobierania po dawnych pięciu minutach.
            timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            _myDrCancellation = timeout;
            await _myDr.RefreshNowAsync(timeout.Token).ConfigureAwait(true);
            if (!_isClosing)
            {
                SetMyDrActionStatus("Dane MyDR zostały odświeżone.", isError: false);
                RefreshMyDrStatus();
            }
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            _store.Log.Info("Ustawienia MyDR", "Anulowano odświeżanie MyDR podczas zamykania okna ustawień.");
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            SetMyDrActionStatus("Pobieranie z MyDR trwało ponad 30 minut. Spróbuj ponownie później.", isError: true);
            _store.Log.Warning("Ustawienia MyDR", "Ręczne odświeżanie MyDR przekroczyło limit 30 minut.", new TimeoutException("MyDR nie odpowiedział w ciągu 30 minut."));
        }
        catch (OperationCanceledException)
        {
            SetMyDrActionStatus("Odświeżanie MyDR zostało przerwane. Spróbuj ponownie.", isError: true);
            _store.Log.Info("Ustawienia MyDR", "Ręczne odświeżanie MyDR zostało przerwane.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("już trwa", StringComparison.OrdinalIgnoreCase))
        {
            SetMyDrActionStatus("Odświeżanie MyDR już trwa w tle. Poczekaj na jego zakończenie.", isError: false);
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia MyDR", "Ręczne odświeżanie MyDR nie powiodło się.", exception);
            if (!_isClosing)
                SetMyDrActionStatus(UserFacingErrors.ForMyDrSynchronization(exception), isError: true);
        }
        finally
        {
            if (ReferenceEquals(_myDrCancellation, timeout)) _myDrCancellation = null;
            timeout?.Dispose();
            if (!_isClosing)
            {
                SetMyDrBusy(false);
                RefreshMyDrStatus();
            }
        }
    }

    private void DeleteMyDrSettings()
    {
        var confirmation = MessageBox.Show(
            this,
            "Czy usunąć zapisane dane dostępowe MyDR? Automatyczne pobieranie obrotu zostanie wyłączone do czasu ponownej konfiguracji.",
            "Usuń dane MyDR",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        try
        {
            _myDrCancellation?.Cancel();
            var applied = _myDr.TryApplyConfigurationChange(() =>
            {
                _store.DeleteMyDrCredentials();
                _store.Log.Info("Ustawienia MyDR", "Usunięto dane dostępowe MyDR.");
                return true;
            }, restartScheduler: true, out _);
            if (!applied)
            {
                SetMyDrActionStatus("Trwa synchronizacja MyDR. Dane będzie można usunąć po jej zakończeniu.", isError: false);
                return;
            }

            _savedMyDrConnectionId = Guid.Empty;
            _myDrClientId.Clear();
            _myDrClientSecret.Clear();
            _myDrRefreshToken.Clear();
            SetMyDrActionStatus("Dane dostępowe MyDR zostały usunięte.", isError: false);
            RefreshMyDrStatus();
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia MyDR", "Nie udało się usunąć danych dostępowych MyDR.", exception);
            SetMyDrActionStatus("Nie udało się usunąć danych MyDR. Spróbuj ponownie.", isError: true);
        }
    }

    private void RefreshMyDrStatus()
    {
        try
        {
            var status = _myDr.GetStatusSnapshot();
            _myDrCredentialsStatus.Text = status.IsConfigured ? "Zapisane" : "Nie skonfigurowano";
            _myDrCredentialsStatus.Foreground = status.IsConfigured ? Brushes.SeaGreen : Brushes.DimGray;
            _myDrLastAttempt.Text = FormatMyDrDate(status.LastAttemptUtc, "Jeszcze nie wykonywano");
            _myDrLastSuccess.Text = FormatMyDrDate(status.LastSuccessfulSyncUtc, "Jeszcze nie zakończono powodzeniem");
            _myDrNextAttempt.Text = status.IsSynchronizing ? "Synchronizacja trwa…" : FormatMyDrDate(status.NextScheduledSyncUtc, "Nie zaplanowano");
            _myDrLastError.Text = string.IsNullOrWhiteSpace(status.LastError) ? "Brak" : SecretRedactor.Redact(status.LastError);
            _myDrLastError.Foreground = string.IsNullOrWhiteSpace(status.LastError) ? Brushes.DimGray : Brushes.Firebrick;
            var busy = status.IsSynchronizing || _myDrCancellation is not null;
            SetMyDrBusy(busy);
            _myDrDeleteButton.IsEnabled = !busy && status.IsConfigured;
            _myDrStatusReadFailed = false;
        }
        catch (Exception exception)
        {
            if (!_myDrStatusReadFailed)
                _store.Log.Error("Ustawienia MyDR", "Nie udało się odczytać stanu synchronizacji MyDR.", exception);
            _myDrStatusReadFailed = true;
            _myDrCredentialsStatus.Text = _savedMyDrConnectionId == Guid.Empty ? "Nie skonfigurowano" : "Zapisane";
            _myDrLastError.Text = "Nie udało się odczytać stanu synchronizacji.";
            _myDrLastError.Foreground = Brushes.Firebrick;
        }
    }

    private static string FormatMyDrDate(DateTimeOffset? value, string emptyText)
    {
        if (value is null) return emptyText;
        var local = TimeZoneInfo.ConvertTime(value.Value, MyDrDailySchedule.WarsawTimeZone);
        return local.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("pl-PL")) + " (czas polski)";
    }

    private AppSettings ReadSettings() => new()
    {
        Nip = NipValidator.Normalize(_nip.Text),
        NotificationsEnabled = _notifications.IsChecked == true
    };

    private string? Validate(bool requireEnteredToken)
    {
        if (!NipValidator.IsValid(_nip.Text)) return "Wprowadź poprawny polski NIP wraz z cyfrą kontrolną.";
        if (_requiresProductionToken && string.IsNullOrWhiteSpace(_token.Password))
            return "Wprowadź token z produkcyjnego KSeF. Zapisany token TEST/DEMO nie zostanie użyty.";
        if (!string.Equals(NipValidator.Normalize(_nip.Text), _originalNip, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(_token.Password))
            return "Po zmianie NIP-u wprowadź token właściwy dla nowego kontekstu KSeF.";
        if (requireEnteredToken && string.IsNullOrWhiteSpace(_token.Password) && string.IsNullOrWhiteSpace(_store.LoadToken()))
            return "Wprowadź token KSeF.";
        return null;
    }

    private async System.Threading.Tasks.Task TestConnectionAsync()
    {
        var validation = Validate(requireEnteredToken: true);
        if (validation is not null)
        {
            SetStatus(validation, isError: true);
            return;
        }

        var token = string.IsNullOrWhiteSpace(_token.Password) ? _store.LoadToken()! : _token.Password;
        SetBusy(true);
        SetStatus("Sprawdzanie uwierzytelnienia…", isError: false);
        _store.Log.Info("Ustawienia", "Rozpoczęto sprawdzanie połączenia z KSeF.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        _testCancellation = timeout;
        try
        {
            _synchronization.EnsureApiRequestsAllowed();
            using var client = new KsefApiClient(
                ReadSettings(),
                token,
                metadataRequestReservation: _synchronization.ReserveMetadataRequestAsync);
            await client.AuthenticateAsync(timeout.Token).ConfigureAwait(true);
            SetStatus("Sprawdzanie uprawnienia InvoiceRead…", isError: false);
            await client.VerifyInvoiceReadAccessAsync(timeout.Token).ConfigureAwait(true);
            SetStatus("Połączenie działa. Token ma uprawnienie InvoiceRead w podanym kontekście.", isError: false);
            _store.Log.Info("Ustawienia", "Sprawdzenie połączenia z KSeF zakończyło się powodzeniem.");
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            _store.Log.Info("Ustawienia", "Anulowano test połączenia podczas zamykania okna ustawień.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_isClosing)
        {
            SetStatus("Przekroczono minutę oczekiwania na odpowiedź KSeF. Spróbuj ponownie.", isError: true);
            _store.Log.Warning("Ustawienia", "Test połączenia z KSeF przekroczył limit jednej minuty.", new TimeoutException("KSeF nie odpowiedział w ciągu jednej minuty."));
        }
        catch (KsefApiException exception)
        {
            _synchronization.RecordApiCooldown(exception);
            _store.Log.Error("Ustawienia", "Test połączenia z KSeF nie powiódł się.", exception);
            if (!_isClosing) SetStatus(UserFacingErrors.ForConnectionTest(exception), isError: true);
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia", "Test połączenia z KSeF nie powiódł się.", exception);
            if (!_isClosing) SetStatus(UserFacingErrors.ForConnectionTest(exception), isError: true);
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, timeout)) _testCancellation = null;
            if (!_isClosing) SetBusy(false);
        }
    }

    private void SaveAndClose()
    {
        var validation = Validate(requireEnteredToken: true);
        if (validation is not null)
        {
            SetStatus(validation, isError: true);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_token.Password)) _store.SaveToken(_token.Password);
            _store.SaveSettings(ReadSettings());
            _store.Log.Info("Ustawienia", "Zapisano ustawienia aplikacji.");
            ConfigurationChanged = true;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            _store.Log.Error("Ustawienia", "Nie udało się zapisać ustawień aplikacji.", exception);
            SetStatus(UserFacingErrors.ForSettingsSave(exception), isError: true);
        }
    }

    private void SetBusy(bool busy)
    {
        _testButton.IsEnabled = !busy;
        _saveButton.IsEnabled = !busy;
    }

    private void SetMyDrBusy(bool busy)
    {
        _myDrClientId.IsEnabled = !busy;
        _myDrClientSecret.IsEnabled = !busy;
        _myDrRefreshToken.IsEnabled = !busy;
        _myDrSaveButton.IsEnabled = !busy;
        _myDrRefreshButton.IsEnabled = !busy;
        _myDrDeleteButton.IsEnabled = !busy && _savedMyDrConnectionId != Guid.Empty;
    }

    private void SetMyDrActionStatus(string text, bool isError)
    {
        _myDrActionStatus.Text = text;
        _myDrActionStatus.Foreground = isError ? Brushes.Firebrick : Brushes.SeaGreen;
    }

    private void SetStatus(string text, bool isError)
    {
        _status.Text = text;
        _status.Foreground = isError ? Brushes.Firebrick : Brushes.SeaGreen;
    }

    private static void AddLabel(Grid form, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 14),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(label, row);
        form.Children.Add(label);
    }

    private static void AddStatusRow(Grid form, string labelText, TextBlock value, int row)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Margin = new Thickness(0, 0, 16, 8),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(label, row);
        form.Children.Add(label);

        value.Margin = new Thickness(0, 0, 0, 8);
        value.TextWrapping = TextWrapping.Wrap;
        value.Foreground = Brushes.DimGray;
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        form.Children.Add(value);
    }
}
