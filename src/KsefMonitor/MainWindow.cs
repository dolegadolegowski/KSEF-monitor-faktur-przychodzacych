using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Ręczne odświeżenie jest granicą UI i musi zamienić każdy błąd na komunikat dla użytkownika.")]
internal sealed class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(20, 73, 122));
    private readonly AppStore _store;
    private readonly SynchronizationService _synchronization;
    private readonly MyDrSynchronizationService _myDrSynchronization;
    private readonly AppUpdateService _updates;
    private readonly DataGrid _grid = new();
    private readonly TextBlock _monthLabel = new();
    private readonly TextBlock _ksefMonthSummary = new();
    private readonly TextBlock _myDrMonthSummary = new();
    private readonly TextBlock _status = new();
    private readonly Button _previousMonth = new();
    private readonly Button _nextMonth = new();
    private readonly Button _refreshButton = new();
    private readonly Button _updateButton = new();
    private readonly Button _versionButton = new();
    private readonly System.Drawing.Icon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StatusBannerState _statusBanner = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, InvoiceDetailsWindow> _detailsWindows = new(StringComparer.Ordinal);
    private DateTime _displayMonth = GetCurrentWarsawMonth();
    private bool _reallyClose;
    private bool _minimizeHintShown;
    private string? _notificationTarget;

    public MainWindow(
        AppStore store,
        SynchronizationService synchronization,
        MyDrSynchronizationService myDrSynchronization,
        AppUpdateService updates)
    {
        _store = store;
        _synchronization = synchronization;
        _myDrSynchronization = myDrSynchronization;
        _updates = updates;
        Title = "KSeF Monitor — Faktury otrzymane";
        Width = 1040;
        Height = 680;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        Content = BuildContent();

        _trayIcon = TrayIconFactory.Create();
        Icon = TrayIconFactory.CreateImageSource(_trayIcon);
        _trayMenu = BuildTrayMenu();
        _notifyIcon = BuildNotifyIcon();
        _synchronization.StatusChanged += OnStatusChanged;
        _synchronization.StateChanged += OnStateChanged;
        _synchronization.NewInvoicesDiscovered += OnNewInvoicesDiscovered;
        _myDrSynchronization.StatusChanged += OnStatusChanged;
        _myDrSynchronization.StateChanged += OnMyDrStateChanged;
        _updates.StateChanged += OnUpdateStateChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        _clock.Tick += (_, _) =>
        {
            UpdateSyncLabels();
            ExpireStatusIfNeeded();
        };
        _clock.Start();
        RefreshRows();
        ApplyUpdateUi(_updates.GetSnapshot());
    }

    private Grid BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 229, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 11, 20, 11)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var monthButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _previousMonth.Content = "←";
        _previousMonth.ToolTip = "Poprzedni miesiąc";
        _previousMonth.Width = 32;
        _previousMonth.Height = 32;
        _previousMonth.Cursor = Cursors.Hand;
        _previousMonth.Click += (_, _) => ChangeMonth(-1);
        monthButtons.Children.Add(_previousMonth);
        _monthLabel.FontSize = 16;
        _monthLabel.FontWeight = FontWeights.SemiBold;
        _monthLabel.VerticalAlignment = VerticalAlignment.Center;
        _monthLabel.Margin = new Thickness(10, 0, 10, 0);
        monthButtons.Children.Add(_monthLabel);
        _nextMonth.Content = "→";
        _nextMonth.ToolTip = "Następny miesiąc";
        _nextMonth.Width = 32;
        _nextMonth.Height = 32;
        _nextMonth.Cursor = Cursors.Hand;
        _nextMonth.Click += (_, _) => ChangeMonth(1);
        monthButtons.Children.Add(_nextMonth);
        headerGrid.Children.Add(monthButtons);

        var summaries = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0)
        };
        ConfigureSummaryText(_ksefMonthSummary);
        ConfigureSummaryText(_myDrMonthSummary);
        _myDrMonthSummary.Margin = new Thickness(0, 3, 0, 0);
        summaries.Children.Add(_ksefMonthSummary);
        summaries.Children.Add(_myDrMonthSummary);
        Grid.SetColumn(summaries, 1);
        headerGrid.Children.Add(summaries);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _refreshButton.Content = "--:--";
        _refreshButton.Width = 90;
        _refreshButton.Padding = new Thickness(10, 7, 10, 7);
        _refreshButton.ToolTip = "Kliknij, aby natychmiast sprawdzić nowe faktury.";
        _refreshButton.Background = Accent;
        _refreshButton.Foreground = Brushes.White;
        _refreshButton.BorderBrush = Accent;
        _refreshButton.FontWeight = FontWeights.SemiBold;
        _refreshButton.Cursor = Cursors.Hand;
        _refreshButton.Click += async (_, _) => await RefreshManuallyAsync().ConfigureAwait(true);
        actions.Children.Add(_refreshButton);
        Grid.SetColumn(actions, 2);
        headerGrid.Children.Add(actions);
        header.Child = headerGrid;
        root.Children.Add(header);

        _grid.ItemsSource = Array.Empty<InvoiceRow>();
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.CanUserResizeRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.EnableRowVirtualization = true;
        _grid.EnableColumnVirtualization = true;
        _grid.RowHeight = 44;
        _grid.RowHeaderWidth = 0;
        _grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(249, 251, 253));
        _grid.Margin = new Thickness(24, 14, 24, 14);
        _grid.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 232));
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(231, 235, 240))));
        _grid.CellStyle = cellStyle;

        var newInvoiceRowStyle = new Style(typeof(DataGridRow));
        var newInvoiceTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.IsNew)),
            Value = true
        };
        newInvoiceTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(230, 244, 234))));
        newInvoiceTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(117, 185, 133))));
        newInvoiceTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 1, 0, 1)));
        newInvoiceRowStyle.Triggers.Add(newInvoiceTrigger);

        var selectedNewInvoiceTrigger = new MultiDataTrigger();
        selectedNewInvoiceTrigger.Conditions.Add(new Condition(
            new System.Windows.Data.Binding(nameof(InvoiceRow.IsNew)),
            true));
        selectedNewInvoiceTrigger.Conditions.Add(new Condition(
            new System.Windows.Data.Binding(nameof(DataGridRow.IsSelected))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self)
            },
            true));
        selectedNewInvoiceTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(205, 235, 213))));
        selectedNewInvoiceTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(29, 57, 38))));
        newInvoiceRowStyle.Triggers.Add(selectedNewInvoiceTrigger);
        _grid.RowStyle = newInvoiceRowStyle;

        var newLabelStyle = new Style(typeof(TextBlock));
        newLabelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Accent));
        newLabelStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        newLabelStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        _grid.Columns.Add(new DataGridTextColumn { Header = string.Empty, Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.NewLabel)), Width = 68, ElementStyle = newLabelStyle });
        var rowTextStyle = new Style(typeof(TextBlock));
        rowTextStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        _grid.Columns.Add(new DataGridTextColumn { Header = "Data wystawienia", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.IssueDate)), Width = 130, ElementStyle = rowTextStyle });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Sprzedawca", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.Seller)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = rowTextStyle });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Numer faktury", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.InvoiceNumber)), Width = 180, ElementStyle = rowTextStyle });
        var amountStyle = new Style(typeof(TextBlock), rowTextStyle);
        amountStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        amountStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Kwota brutto",
            Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.GrossAmount)),
            SortMemberPath = nameof(InvoiceRow.GrossAmountSortValue),
            Width = 155,
            ElementStyle = amountStyle
        });
        _grid.PreviewMouseLeftButtonUp += OnGridMouseLeftButtonUp;
        _grid.PreviewKeyDown += OnGridPreviewKeyDown;
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var footer = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 229, 235)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 10, 24, 10)
        };
        _status.Text = "Gotowy.";
        _status.Foreground = Brushes.DimGray;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(_status);
        var footerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _updateButton.Content = "Aktualizuj";
        _updateButton.FontSize = 11;
        _updateButton.Padding = new Thickness(8, 3, 8, 3);
        _updateButton.Margin = new Thickness(0, 0, 6, 0);
        _updateButton.Cursor = Cursors.Hand;
        _updateButton.Background = new SolidColorBrush(Color.FromRgb(36, 122, 72));
        _updateButton.Foreground = Brushes.White;
        _updateButton.BorderBrush = new SolidColorBrush(Color.FromRgb(36, 122, 72));
        _updateButton.Visibility = Visibility.Collapsed;
        _updateButton.Click += async (_, _) => await InstallUpdateAsync().ConfigureAwait(true);
        footerActions.Children.Add(_updateButton);

        _versionButton.Content = GetDisplayVersion();
        _versionButton.FontSize = 11;
        _versionButton.Padding = new Thickness(8, 3, 8, 3);
        _versionButton.Cursor = Cursors.Hand;
        _versionButton.ToolTip = "Otwórz ustawienia aplikacji";
        _versionButton.Click += (_, _) => OpenSettings();
        footerActions.Children.Add(_versionButton);
        Grid.SetColumn(footerActions, 1);
        footerGrid.Children.Add(footerActions);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Otwórz", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Odśwież", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshManuallyAsync().ConfigureAwait(true)));
        menu.Items.Add("Ustawienia", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Wyjdź", null, (_, _) => Dispatcher.Invoke(() => ((App)System.Windows.Application.Current).ExitApplication()));
        return menu;
    }

    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Nazwa produktu KSeF Monitor nie podlega lokalizacji.")]
    private Forms.NotifyIcon BuildNotifyIcon()
    {
        var icon = new Forms.NotifyIcon
        {
            Text = "KSeF Monitor",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        icon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            if (_notificationTarget is not null && _synchronization.TryGetInvoice(_notificationTarget, out var target) && target is not null)
                OpenInvoice(target);
        });
        return icon;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _synchronization.Start();
        _myDrSynchronization.Start();
        _ = CheckForUpdatesAtStartupAsync();
        if (_store.ConsumeLoadWarning() is not null)
            ShowStatus(new AppStatusMessage(
                "Nie udało się wczytać części zapisanych danych. Aplikacja użyła bezpiecznej kopii lub ustawień domyślnych.",
                StatusSeverity.Error));
        if (!_synchronization.IsConfigured)
            Dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.ApplicationIdle);
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAtStartupAsync()
    {
        try
        {
            await _updates.CheckForUpdatesAsync(force: false).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _store.Log.Warning("Aktualizacja", "Nieoczekiwany błąd wywołania sprawdzania przy starcie.", exception);
        }
    }

    private async System.Threading.Tasks.Task InstallUpdateAsync()
    {
        var snapshot = _updates.GetSnapshot();
        if (snapshot.AvailableRelease is not { } release) return;
        var answer = System.Windows.MessageBox.Show(
            $"Zainstalować KSeF Monitor v{release.Version}?\n\nAplikacja pobierze plik z GitHuba, sprawdzi jego integralność, zamknie się i uruchomi ponownie.",
            "Aktualizacja KSeF Monitor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        _updateButton.IsEnabled = false;
        try
        {
            await _updates.PrepareAndLaunchUpdateAsync().ConfigureAwait(true);
            ((App)System.Windows.Application.Current).ExitApplication();
        }
        catch (AppUpdateException exception)
        {
            ShowStatus(new AppStatusMessage(exception.UserMessage, StatusSeverity.Error));
            System.Windows.MessageBox.Show(
                exception.UserMessage,
                "Nie udało się zainstalować aktualizacji",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            _store.Log.Error("Aktualizacja", "Nieoczekiwany błąd obsługi aktualizacji w interfejsie.", exception);
            const string message = "Nie udało się zainstalować aktualizacji. Aplikacja nie została zmieniona.";
            ShowStatus(new AppStatusMessage(message, StatusSeverity.Error));
            System.Windows.MessageBox.Show(message, "Nie udało się zainstalować aktualizacji", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_reallyClose && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                ApplyUpdateUi(_updates.GetSnapshot());
        }
    }

    private async System.Threading.Tasks.Task RefreshManuallyAsync()
    {
        _refreshButton.IsEnabled = false;
        try
        {
            await _synchronization.RefreshNowAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                UserFacingErrors.ForSynchronization(exception),
                "Nie udało się odświeżyć",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _refreshButton.IsEnabled = !_synchronization.IsSynchronizing;
        }
    }

    private void OpenSettings()
    {
        ShowFromTray();
        var window = new SettingsWindow(_store, _myDrSynchronization, _updates) { Owner = this };
        window.ShowDialog();
        if (window.ConfigurationChanged)
        {
            ShowStatus(new AppStatusMessage("Ustawienia KSeF zapisane. Oczekiwanie na synchronizację…"));
            _synchronization.UpdateConfiguration();
        }
        RefreshRows();
    }

    private void ChangeMonth(int offset)
    {
        var candidate = _displayMonth.AddMonths(offset);
        var current = GetCurrentWarsawMonth();
        var oldest = current.AddMonths(-SynchronizationService.VisibleHistoryMonthsBack);
        if (candidate < oldest || candidate > current) return;
        _displayMonth = candidate;
        RefreshRows();
    }

    private void RefreshRows()
    {
        var current = GetCurrentWarsawMonth();
        var oldest = current.AddMonths(-SynchronizationService.VisibleHistoryMonthsBack);
        if (_displayMonth < oldest || _displayMonth > current) _displayMonth = current;

        var culture = CultureInfo.GetCultureInfo("pl-PL");
        _monthLabel.Text = culture.TextInfo.ToTitleCase(_displayMonth.ToString("MMMM yyyy", culture));
        _previousMonth.IsEnabled = _displayMonth > oldest;
        _nextMonth.IsEnabled = _displayMonth < current;

        var snapshot = _synchronization.GetInvoicesSnapshot();
        var items = snapshot
            .Where(x => x.IssueDate.Year == _displayMonth.Year && x.IssueDate.Month == _displayMonth.Month)
            .OrderByDescending(x => x.IsNew)
            .ThenByDescending(x => x.IssueDate)
            .ThenByDescending(x => x.PermanentStorageDate)
            .Select(x => new InvoiceRow { Source = x })
            .ToList();

        _grid.ItemsSource = items;
        var grossTotals = MonthlyInvoiceSummary.FormatGrossTotals(items.Select(item => item.Source));
        var invoiceCounts = items.Count == 0
            ? "Brak faktur"
            : $"Faktury: {items.Count}  •  Nowe: {items.Count(x => x.Source.IsNew)}";
        var grossValue = grossTotals.Replace("Łącznie brutto: ", string.Empty, StringComparison.Ordinal);
        _ksefMonthSummary.Inlines.Clear();
        _ksefMonthSummary.Inlines.Add(new Run("Faktury kosztowe: ")
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });
        _ksefMonthSummary.Inlines.Add(new Run(grossValue)
        {
            FontSize = _monthLabel.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 55, 70))
        });
        _ksefMonthSummary.Inlines.Add(new Run($"  •  {invoiceCounts}") { FontSize = 12.5 });
        _ksefMonthSummary.ToolTip = $"{invoiceCounts}  •  {grossTotals}\nKwoty w różnych walutach są sumowane oddzielnie.";
        RefreshMyDrSummary();
        var allNewCount = snapshot.Count(x => x.IsNew);
        _notifyIcon.Text = allNewCount == 0 ? "KSeF Monitor" : $"KSeF Monitor — nowe: {allNewCount}";
        UpdateSyncLabels();
    }

    private void UpdateSyncLabels()
    {
        var lastSyncText = _synchronization.GetLastSuccessfulSyncUtc() is { } last
            ? $"Ostatnie odświeżenie: {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Jeszcze nie odświeżono";
        _refreshButton.ToolTip = $"{lastSyncText}\nKliknij, aby natychmiast sprawdzić nowe faktury.";

        if (_synchronization.IsSynchronizing)
        {
            _refreshButton.Content = "00:00";
            _refreshButton.ToolTip = $"{lastSyncText}\nTrwa odświeżanie faktur.";
            _refreshButton.IsEnabled = false;
            return;
        }

        _refreshButton.IsEnabled = _synchronization.IsConfigured;
        if (_synchronization.NextScheduledSyncUtc is not { } next)
        {
            _refreshButton.Content = "--:--";
            return;
        }

        var remaining = next - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        _refreshButton.Content = FormatCountdown(remaining);
    }

    private static string GetDisplayVersion()
    {
        return ProductInformation.DisplayVersion;
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_reallyClose || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!_reallyClose && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                ApplyUpdateUi(_updates.GetSnapshot());
        });
    }

    private void ApplyUpdateUi(AppUpdateSnapshot snapshot)
    {
        _versionButton.ToolTip = snapshot.Phase switch
        {
            _ when snapshot.HasError => $"{ProductInformation.DisplayVersion} • {snapshot.Message}\nKliknij, aby otworzyć ustawienia.",
            AppUpdatePhase.Checking => $"{ProductInformation.DisplayVersion} • sprawdzanie aktualizacji…\nKliknij, aby otworzyć ustawienia.",
            AppUpdatePhase.UpToDate => $"{ProductInformation.DisplayVersion} • najnowsza wersja\nKliknij, aby otworzyć ustawienia.",
            AppUpdatePhase.Available or AppUpdatePhase.Downloading or AppUpdatePhase.Preparing or AppUpdatePhase.ReadyToRestart
                when snapshot.AvailableRelease is { } release =>
                $"Zainstalowana: {ProductInformation.DisplayVersion} • dostępna: v{release.Version}\nKliknij, aby otworzyć ustawienia.",
            AppUpdatePhase.Failed => $"{ProductInformation.DisplayVersion} • nie udało się sprawdzić lub przygotować aktualizacji\nKliknij, aby otworzyć ustawienia.",
            _ => $"{ProductInformation.DisplayVersion}\nKliknij, aby otworzyć ustawienia."
        };

        if (!snapshot.HasAvailableUpdate)
        {
            _updateButton.Visibility = Visibility.Collapsed;
            return;
        }

        _updateButton.Visibility = Visibility.Visible;
        _updateButton.ToolTip = snapshot.Message ?? $"Zainstaluj v{snapshot.AvailableRelease!.Version}.";
        switch (snapshot.Phase)
        {
            case AppUpdatePhase.Checking:
                _updateButton.Content = "Sprawdzanie…";
                _updateButton.IsEnabled = false;
                break;
            case AppUpdatePhase.Downloading:
                _updateButton.Content = snapshot.ProgressPercent is { } percent ? $"Pobieranie {percent}%" : "Pobieranie…";
                _updateButton.IsEnabled = false;
                break;
            case AppUpdatePhase.Preparing:
                _updateButton.Content = "Instalowanie…";
                _updateButton.IsEnabled = false;
                break;
            case AppUpdatePhase.ReadyToRestart:
                _updateButton.Content = "Restart…";
                _updateButton.IsEnabled = false;
                break;
            default:
                _updateButton.Content = "Aktualizuj";
                _updateButton.IsEnabled = true;
                break;
        }
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        return $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void OnGridMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is not DataGridRow row) return;
        if (row.Item is InvoiceRow invoiceRow) OpenInvoice(invoiceRow.Source);
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || _grid.SelectedItem is not InvoiceRow row) return;
        e.Handled = true;
        OpenInvoice(row.Source);
    }

    private void OpenInvoice(StoredInvoice invoice)
    {
        if (_detailsWindows.TryGetValue(invoice.KsefNumber, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var details = new InvoiceDetailsWindow(invoice, _store.Log, _synchronization.EnsureInvoiceXmlAsync) { Owner = this };
        _detailsWindows[invoice.KsefNumber] = details;
        details.Closed += (_, _) => _detailsWindows.Remove(invoice.KsefNumber);
        details.InvoiceContentDisplayed += (_, _) => _synchronization.MarkViewed(invoice.KsefNumber);
        details.Show();
    }

    private void OnStatusChanged(object? sender, AppStatusMessage status) =>
        Dispatcher.InvokeAsync(() => ShowStatus(status));

    private void ShowStatus(AppStatusMessage status)
    {
        _statusBanner.Apply(status, DateTimeOffset.UtcNow);
        _status.Text = status.Text;
        _status.Foreground = status.IsError ? Brushes.Firebrick : Brushes.DimGray;
        _status.FontWeight = status.IsError ? FontWeights.SemiBold : FontWeights.Normal;
    }

    internal void ShowStatusMessage(AppStatusMessage status) => ShowStatus(status);

    private void ExpireStatusIfNeeded()
    {
        if (!_statusBanner.Expire(DateTimeOffset.UtcNow)) return;
        _status.Text = string.Empty;
        _status.Foreground = Brushes.DimGray;
        _status.FontWeight = FontWeights.Normal;
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _refreshButton.IsEnabled = !_synchronization.IsSynchronizing;
        RefreshRows();
    });

    private void OnMyDrStateChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshMyDrSummary);

    private void RefreshMyDrSummary()
    {
        var status = _myDrSynchronization.GetStatusSnapshot();
        var summary = _myDrSynchronization.GetMonthSummary(_displayMonth.Year, _displayMonth.Month);
        _myDrMonthSummary.Inlines.Clear();
        _myDrMonthSummary.Inlines.Add(new Run("Obrót MyDR: ")
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });

        if (!status.IsConfigured)
        {
            _myDrMonthSummary.Inlines.Add(new Run("skonfiguruj połączenie w ustawieniach") { FontSize = 12.5 });
            _myDrMonthSummary.ToolTip = "Wprowadź Client ID, Client Secret i Refresh Token w zakładce MyDR.";
            return;
        }

        if (summary is null)
        {
            if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                _myDrMonthSummary.Inlines.Add(new Run("brak danych — ostatnia próba nieudana")
                {
                    FontSize = 12.5,
                    Foreground = Brushes.Firebrick
                });
                _myDrMonthSummary.ToolTip = SecretRedactor.Redact(status.LastError) +
                    "\nUżyj „Odśwież teraz” w ustawieniach, aby spróbować ponownie przed kolejnym dniem.";
                return;
            }

            var pending = status.IsSynchronizing ? "trwa obliczanie…" : "oczekuje na pierwsze dzienne sprawdzenie";
            _myDrMonthSummary.Inlines.Add(new Run(pending) { FontSize = 12.5 });
            _myDrMonthSummary.ToolTip = status.LastAttemptUtc is { } attempt
                ? $"Ostatnia próba: {TimeZoneInfo.ConvertTime(attempt, MyDrDailySchedule.WarsawTimeZone):dd.MM.yyyy HH:mm} (czas polski)."
                : "Dane zostaną pobrane podczas pierwszego dziennego sprawdzenia.";
            return;
        }

        _myDrMonthSummary.Inlines.Add(new Run($"{summary.GrossAmount:N2} PLN")
        {
            FontSize = _monthLabel.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 55, 70))
        });
        _myDrMonthSummary.Inlines.Add(new Run($"  •  wizyty: {summary.VisitCount}  •  usługi: {summary.ServiceCount}")
        {
            FontSize = 12.5
        });
        if (!string.IsNullOrWhiteSpace(status.LastError))
            _myDrMonthSummary.Inlines.Add(new Run("  •  ostatnia próba nieudana")
            {
                FontSize = 12,
                Foreground = Brushes.Firebrick
            });

        var lastSuccess = status.LastSuccessfulSyncUtc is { } success
            ? TimeZoneInfo.ConvertTime(success, MyDrDailySchedule.WarsawTimeZone)
                .ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("pl-PL"))
            : "brak";
        _myDrMonthSummary.ToolTip =
            $"Ostatnie poprawne odświeżenie: {lastSuccess}.\n" +
            "Suma pola value usług prywatnych dla wykonanych wizyt: Do rozliczenia, Oczekuje na płatność, Zakończona, Zamknięta lub Archiwalna. Dane są sprawdzane raz dziennie.";
    }

    private static void ConfigureSummaryText(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Foreground = new SolidColorBrush(Color.FromRgb(70, 81, 94));
        text.FontSize = 12.5;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private static DateTime GetCurrentWarsawMonth()
    {
        var today = MyDrDailySchedule.GetWarsawDate(DateTimeOffset.UtcNow);
        return new DateTime(today.Year, today.Month, 1);
    }

    private void OnNewInvoicesDiscovered(object? sender, IReadOnlyList<StoredInvoice> invoices) => Dispatcher.InvokeAsync(() =>
    {
        var settings = _store.LoadSettings();
        if (!settings.NotificationsEnabled) return;
        var notNotified = new List<StoredInvoice>();
        foreach (var discovered in invoices)
            if (_synchronization.TryGetInvoice(discovered.KsefNumber, out var current) &&
                current is { NotifiedAtUtc: null })
                notNotified.Add(current);
        if (notNotified.Count == 0) return;
        _notificationTarget = notNotified[0].KsefNumber;

        if (notNotified.Count == 1)
        {
            var invoice = notNotified[0];
            var seller = string.IsNullOrWhiteSpace(invoice.SellerName) ? invoice.SellerNip : invoice.SellerName;
            _notifyIcon.ShowBalloonTip(8000, "Nowa faktura w KSeF", $"{seller} — {invoice.GrossAmount:N2} {invoice.Currency}", Forms.ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(8000, "Nowe faktury w KSeF", $"Odebrano {notNotified.Count} nowych faktur.", Forms.ToolTipIcon.Info);
        }
        _synchronization.MarkNotified(notNotified.Select(x => x.KsefNumber));
    });

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        Hide();
        if (!_minimizeHintShown)
        {
            _minimizeHintShown = true;
            _notifyIcon.ShowBalloonTip(2500, "KSeF Monitor", "Aplikacja nadal działa i sprawdza faktury co 15 minut.", Forms.ToolTipIcon.Info);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void PrepareForExit()
    {
        if (_reallyClose) return;
        _reallyClose = true;
        _clock.Stop();
        _synchronization.StatusChanged -= OnStatusChanged;
        _synchronization.StateChanged -= OnStateChanged;
        _synchronization.NewInvoicesDiscovered -= OnNewInvoicesDiscovered;
        _myDrSynchronization.StatusChanged -= OnStatusChanged;
        _myDrSynchronization.StateChanged -= OnMyDrStateChanged;
        _updates.StateChanged -= OnUpdateStateChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
        _trayIcon.Dispose();
        Close();
    }
}
