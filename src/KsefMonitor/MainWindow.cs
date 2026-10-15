using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
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
    private readonly ObservableCollection<InvoiceRow> _rows = new();
    private readonly TextBox _search = new();
    private readonly CheckBox _onlyNew = new() { Content = "Tylko NOWE" };
    private readonly CheckBox _onlyCorrections = new() { Content = "Korekty" };
    private readonly ComboBox _currency = new() { MinWidth = 85 };
    private readonly TextBlock _filteredSummary = new();
    private readonly TextBlock _freshness = new();
    private readonly TextBlock _emptyMessage = new();
    private string _ksefActivity = string.Empty;
    private bool _ksefFailed;
    private bool _refreshingRows;
    private WindowState _lastVisibleState = WindowState.Normal;
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
    private readonly DispatcherTimer _myDrPopupCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly StatusBannerState _statusBanner = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, InvoiceDetailsWindow> _detailsWindows = new(StringComparer.Ordinal);
    private DateTime _displayMonth = GetCurrentWarsawMonth();
    private Popup? _myDrDoctorTurnoverPopup;
    private FrameworkElement? _myDrDoctorTurnoverSource;
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
        Width = Math.Min(1040, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(680, SystemParameters.WorkArea.Height - 24);
        MinWidth = 600;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        Content = BuildContent();
        RestoreWindowPlacement();

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
        _myDrPopupCloseTimer.Tick += (_, _) =>
        {
            _myDrPopupCloseTimer.Stop();
            if (!IsMyDrDoctorTurnoverPopupActive()) HideMyDrDoctorTurnoverPopup();
        };
        _clock.Start();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && (e.PreviousSize.Width < 960) != (e.NewSize.Width < 960)) RefreshRows();
        };
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

        _grid.ItemsSource = _rows;
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
        _grid.Columns.Add(new DataGridTextColumn { Header = "Data wystawienia", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.IssueDate)), SortMemberPath = nameof(InvoiceRow.IssueDateSortValue), Width = 130, ElementStyle = rowTextStyle });
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
        var listArea = new Grid();
        listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listArea.RowDefinitions.Add(new RowDefinition());
        var filters = new DockPanel { Margin = new Thickness(24, 10, 24, 0), LastChildFill = true };
        var choices = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var choice in new[] { _onlyNew, _onlyCorrections })
        {
            choice.Margin = new Thickness(12, 4, 0, 4);
            choice.VerticalAlignment = VerticalAlignment.Center;
            choice.Checked += (_, _) => ApplyLocalFilters();
            choice.Unchecked += (_, _) => ApplyLocalFilters();
            choices.Children.Add(choice);
        }
        _currency.Items.Add("Wszystkie waluty");
        _currency.SelectedIndex = 0;
        _currency.Margin = new Thickness(12, 0, 0, 0);
        _currency.SelectionChanged += (_, _) => ApplyLocalFilters();
        choices.Children.Add(_currency);
        var clearFilters = new Button { Content = "Wyczyść", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 0, 0, 0) };
        clearFilters.Click += (_, _) =>
        {
            _refreshingRows = true;
            _search.Clear();
            _onlyNew.IsChecked = false;
            _onlyCorrections.IsChecked = false;
            _currency.SelectedIndex = 0;
            _refreshingRows = false;
            ApplyLocalFilters();
        };
        choices.Children.Add(clearFilters);
        DockPanel.SetDock(choices, Dock.Right);
        filters.Children.Add(choices);
        var searchLabel = new Label { Content = "_Szukaj:", Target = _search, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(searchLabel, Dock.Left);
        filters.Children.Add(searchLabel);
        _search.ToolTip = "Szukaj po sprzedawcy, NIP-ie, numerze faktury lub numerze KSeF. Ctrl+F";
        _search.MinWidth = 110;
        _search.Padding = new Thickness(7, 5, 7, 5);
        _search.TextChanged += (_, _) => ApplyLocalFilters();
        AutomationProperties.SetName(_search, "Szukaj faktury po sprzedawcy, NIP-ie lub numerze");
        filters.Children.Add(_search);
        listArea.Children.Add(filters);
        Grid.SetRow(_grid, 1);
        listArea.Children.Add(_grid);
        _emptyMessage.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyMessage.VerticalAlignment = VerticalAlignment.Center;
        _emptyMessage.TextAlignment = TextAlignment.Center;
        _emptyMessage.TextWrapping = TextWrapping.Wrap;
        _emptyMessage.MaxWidth = 500;
        _emptyMessage.Margin = new Thickness(30);
        _emptyMessage.IsHitTestVisible = false;
        Grid.SetRow(_emptyMessage, 1);
        listArea.Children.Add(_emptyMessage);
        Grid.SetRow(listArea, 1);
        root.Children.Add(listArea);
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.SortDescriptions.Add(new SortDescription(nameof(InvoiceRow.IssueDateSortValue), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(InvoiceRow.StorageDateSortValue), ListSortDirection.Descending));

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
        var statusPanel = new StackPanel();
        _filteredSummary.FontSize = 11;
        _filteredSummary.TextWrapping = TextWrapping.Wrap;
        _freshness.FontSize = 11;
        _freshness.TextWrapping = TextWrapping.Wrap;
        _freshness.Foreground = Brushes.DimGray;
        statusPanel.Children.Add(_filteredSummary);
        statusPanel.Children.Add(_freshness);
        statusPanel.Children.Add(_status);
        footerGrid.Children.Add(statusPanel);
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
        AutomationProperties.SetName(_versionButton, "Ustawienia aplikacji i numer wersji");
        AutomationProperties.SetName(_previousMonth, "Poprzedni miesiąc. Alt+strzałka w lewo");
        AutomationProperties.SetName(_nextMonth, "Następny miesiąc. Alt+strzałka w prawo");
        AutomationProperties.SetName(_refreshButton, "Odśwież faktury. F5");
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
            else if (key == Key.F5) { _ = RefreshManuallyAsync(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right)
            { ChangeMonth(key == Key.Left ? -1 : 1); e.Handled = true; }
        };
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

    private void RestoreWindowPlacement()
    {
        var saved = _store.LoadWindowPlacement();
        if (saved is null || !double.IsFinite(saved.Left + saved.Top + saved.Width + saved.Height) ||
            saved.Width < MinWidth || saved.Height < MinHeight) return;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        if (!desktop.Contains(bounds)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;
        if (saved.Maximized) WindowState = _lastVisibleState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        _store.SaveWindowPlacement(new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            _lastVisibleState == WindowState.Maximized));
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
        var window = new SettingsWindow(_store, _synchronization, _myDrSynchronization, _updates) { Owner = this };
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
        var selected = _grid.SelectedItem as InvoiceRow;
        var scroll = FindVisualDescendant<ScrollViewer>(_grid);
        var verticalOffset = scroll?.VerticalOffset ?? 0;
        var horizontalOffset = scroll?.HorizontalOffset ?? 0;
        _refreshingRows = true;
        var byNumber = _rows.ToDictionary(row => row.Source.KsefNumber, StringComparer.Ordinal);
        var validNumbers = snapshot.Select(invoice => invoice.KsefNumber).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in _rows.Where(row => !validNumbers.Contains(row.Source.KsefNumber)).ToList()) _rows.Remove(obsolete);
        foreach (var invoice in snapshot)
            if (byNumber.TryGetValue(invoice.KsefNumber, out var row)) row.Update(invoice);
            else _rows.Add(new InvoiceRow { Source = invoice });
        var selectedCurrency = _currency.SelectedItem as string;
        var currencies = snapshot.Select(invoice => invoice.Currency).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (!_currency.Items.Cast<string>().Skip(1).SequenceEqual(currencies))
        {
            _currency.Items.Clear();
            _currency.Items.Add("Wszystkie waluty");
            foreach (var currency in currencies) _currency.Items.Add(currency);
            _currency.SelectedItem = selectedCurrency is not null && _currency.Items.Contains(selectedCurrency)
                ? selectedCurrency : "Wszystkie waluty";
        }
        _refreshingRows = false;
        var items = snapshot
            .Where(x => x.IssueDate.Year == _displayMonth.Year && x.IssueDate.Month == _displayMonth.Month)
            .OrderByDescending(x => x.IsNew)
            .ThenByDescending(x => x.IssueDate)
            .ThenByDescending(x => x.PermanentStorageDate)
            .Select(x => new InvoiceRow { Source = x })
            .ToList();

        ApplyLocalFilters();
        if (selected is not null && _grid.Items.Contains(selected)) _grid.SelectedItem = selected;
        if (scroll is not null) Dispatcher.BeginInvoke(() =>
        {
            scroll.ScrollToVerticalOffset(verticalOffset);
            scroll.ScrollToHorizontalOffset(horizontalOffset);
        }, DispatcherPriority.Loaded);
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
        _ksefMonthSummary.Inlines.Add(new Run(ActualWidth > 0 && ActualWidth < 960 ? "" : $"  •  {invoiceCounts}") { FontSize = 12.5 });
        _ksefMonthSummary.ToolTip = $"{invoiceCounts}  •  {grossTotals}\nKwoty w różnych walutach są sumowane oddzielnie.";
        RefreshMyDrSummary();
        var allNewCount = snapshot.Count(x => x.IsNew);
        _notifyIcon.Text = allNewCount == 0 ? "KSeF Monitor" : $"KSeF Monitor — nowe: {allNewCount}";
        UpdateSyncLabels();
    }

    private void ApplyLocalFilters()
    {
        if (_refreshingRows) return;
        var currency = _currency.SelectedIndex > 0 ? _currency.SelectedItem as string : null;
        var filter = new InvoiceListFilter(_displayMonth.Year, _displayMonth.Month, _search.Text,
            _onlyNew.IsChecked == true, _onlyCorrections.IsChecked == true, currency);
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = item => item is InvoiceRow row && filter.Matches(row.Source);
        var visible = view.Cast<InvoiceRow>().ToList();
        _filteredSummary.Text = $"Widoczne: {visible.Count}  •  " +
            MonthlyInvoiceSummary.FormatGrossTotals(visible.Select(row => row.Source)).Replace("Łącznie brutto:", "Suma widocznych:", StringComparison.Ordinal);
        _emptyMessage.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyMessage.Text = !_synchronization.IsConfigured
            ? "Połącz aplikację z KSeF.\nKliknij numer wersji w prawym dolnym rogu, aby otworzyć ustawienia."
            : _synchronization.IsSynchronizing ? "Trwa pobieranie faktur…"
            : _synchronization.GetLastSuccessfulSyncUtc() is null ? "Lista nie została jeszcze pobrana.\nKliknij przycisk odświeżania."
            : _search.Text.Length > 0 || _onlyNew.IsChecked == true || _onlyCorrections.IsChecked == true || currency is not null
                ? "Brak faktur pasujących do filtrów.\nZmień wyszukiwanie lub wyłącz filtry."
                : "Brak faktur w wybranym miesiącu.";
    }

    private void UpdateSyncLabels()
    {
        var lastSyncText = _synchronization.GetLastSuccessfulSyncUtc() is { } last
            ? $"Ostatnie odświeżenie: {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Jeszcze nie odświeżono";
        _refreshButton.ToolTip = $"{lastSyncText}\nKliknij, aby natychmiast sprawdzić nowe faktury.";
        var myDr = _myDrSynchronization.GetStatusSnapshot();
        var myDrDate = myDr.LastSuccessfulSyncUtc is { } md ? md.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.GetCultureInfo("pl-PL")) : "brak danych";
        _freshness.Text = $"KSeF: {lastSyncText.Replace("Ostatnie odświeżenie: ", "", StringComparison.Ordinal)}" +
            (_ksefFailed ? " — ostatnia próba nieudana" : "") + $"  •  MyDR: {myDrDate}" +
            (myDr.IsSynchronizing ? " — obliczanie…" : !myDr.IsConfigured ? " — nieskonfigurowany" :
                !string.IsNullOrEmpty(myDr.LastError) ? " — ostatnia próba nieudana" : "");

        if (_synchronization.IsSynchronizing)
        {
            _refreshButton.Content = "•••";
            _refreshButton.ToolTip = $"{lastSyncText}\n{_ksefActivity}";
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
        Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(sender, _synchronization))
            {
                _ksefActivity = status.Text;
                _ksefFailed = status.IsError;
            }
            ShowStatus(status);
            UpdateSyncLabels();
        });

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
        CloseMyDrDoctorTurnoverPopup();
        var status = _myDrSynchronization.GetStatusSnapshot();
        var summary = _myDrSynchronization.GetMonthSummary(_displayMonth.Year, _displayMonth.Month);
        _myDrMonthSummary.Inlines.Clear();
        _myDrMonthSummary.ToolTip = null;
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

        var amountText = new TextBlock
        {
            Text = $"{summary.GrossAmount:N2} PLN",
            FontSize = _monthLabel.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 55, 70)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var amountTarget = new Button
        {
            Content = amountText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Cursor = Cursors.Help,
            Focusable = true,
            IsTabStop = true,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(amountTarget,
            $"Obrót MyDR {summary.GrossAmount:N2} PLN. Pokaż podział według lekarza lub personelu.");
        AutomationProperties.SetHelpText(amountTarget,
            "Najedź myszą albo ustaw fokus klawiaturą, aby zobaczyć miesięczny obrót poszczególnych osób.");
        ConfigureMyDrDoctorTurnoverPopup(amountTarget, summary, status);
        _myDrMonthSummary.Inlines.Add(new InlineUIContainer(amountTarget)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
        _myDrMonthSummary.Inlines.Add(new Run(ActualWidth > 0 && ActualWidth < 960 ? "" : $"  •  wizyty: {summary.VisitCount}  •  usługi: {summary.ServiceCount}")
        {
            FontSize = 12.5
        });
        if (!string.IsNullOrWhiteSpace(status.LastError))
            _myDrMonthSummary.Inlines.Add(new Run("  •  ostatnia próba nieudana")
            {
                FontSize = 12,
                Foreground = Brushes.Firebrick
            });

    }

    private static Border BuildMyDrDoctorTurnoverPopupContent(
        MyDrMonthSummary summary,
        MyDrSyncStatus status)
    {
        var culture = CultureInfo.GetCultureInfo("pl-PL");
        var monthName = new DateTime(summary.Year, summary.Month, 1)
            .ToString("MMMM yyyy", culture);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = $"Obrót według lekarza/personelu — {monthName}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 45, 61)),
            Margin = new Thickness(0, 0, 0, 9)
        });

        var visibleDoctors = MyDrDoctorTurnoverPresentation.GetVisibleRows(summary);

        if (!summary.DoctorBreakdownAvailable)
        {
            content.Children.Add(CreateMyDrTooltipMessage(
                "Podział według lekarzy pojawi się po najbliższym poprawnym odświeżeniu MyDR."));
        }
        else if (visibleDoctors.Count == 0)
        {
            content.Children.Add(CreateMyDrTooltipMessage(
                "Brak lekarzy z obrotem różnym od 0,00 PLN w tym miesiącu."));
        }
        else
        {
            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddMyDrTooltipCell(table, new TextBlock
            {
                Text = "Lekarz / personel",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(91, 102, 116)),
                Margin = new Thickness(0, 0, 14, 5)
            }, 0, 0);
            AddMyDrTooltipCell(table, new TextBlock
            {
                Text = "Obrót",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(91, 102, 116)),
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(8, 0, 0, 5)
            }, 0, 1);

            for (var index = 0; index < visibleDoctors.Count; index++)
            {
                var doctor = visibleDoctors[index];
                var row = index + 1;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddMyDrTooltipCell(table, new TextBlock
                {
                    Text = doctor.DisplayName,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(31, 45, 61)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 245,
                    Margin = new Thickness(0, 3, 14, 3)
                }, row, 0);
                AddMyDrTooltipCell(table, new TextBlock
                {
                    Text = $"{doctor.GrossAmount:N2} PLN",
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(31, 45, 61)),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 3, 0, 3)
                }, row, 1);
            }

            content.Children.Add(new ScrollViewer
            {
                Content = table,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 300
            });
        }

        var lastSuccess = status.LastSuccessfulSyncUtc is { } success
            ? TimeZoneInfo.ConvertTime(success, MyDrDailySchedule.WarsawTimeZone)
                .ToString("dd.MM.yyyy HH:mm", culture)
            : "brak";
        var footer = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        footer.Children.Add(new TextBlock
        {
            Text = $"Ostatnie poprawne odświeżenie: {lastSuccess}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(91, 102, 116))
        });
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            footer.Children.Add(new TextBlock
            {
                Text = "Ostatnia próba nie powiodła się — pokazano ostatnie poprawne dane.",
                FontSize = 10.5,
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        content.Children.Add(footer);

        return new Border
        {
            MaxWidth = 420,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 212, 222)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.22
            }
        };
    }

    private void ConfigureMyDrDoctorTurnoverPopup(
        Button source,
        MyDrMonthSummary summary,
        MyDrSyncStatus status)
    {
        var content = BuildMyDrDoctorTurnoverPopupContent(summary, status);
        var scrollViewer = FindVisualDescendant<ScrollViewer>(content);
        if (scrollViewer is not null)
        {
            scrollViewer.Focusable = true;
            scrollViewer.IsTabStop = true;
        }
        var popup = new Popup
        {
            PlacementTarget = source,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Child = content
        };
        _myDrDoctorTurnoverSource = source;
        _myDrDoctorTurnoverPopup = popup;

        source.MouseEnter += (_, _) => OpenMyDrDoctorTurnoverPopup();
        source.MouseLeave += (_, _) => ScheduleMyDrDoctorTurnoverPopupClose();
        source.GotKeyboardFocus += (_, _) => OpenMyDrDoctorTurnoverPopup();
        source.LostKeyboardFocus += (_, _) => ScheduleMyDrDoctorTurnoverPopupClose();
        source.Click += (_, _) => OpenMyDrDoctorTurnoverPopup();
        source.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                HideMyDrDoctorTurnoverPopup();
                eventArgs.Handled = true;
                return;
            }

            if (eventArgs.Key is not (Key.Enter or Key.Space or Key.Down) || scrollViewer is null) return;
            OpenMyDrDoctorTurnoverPopup();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => scrollViewer.Focus());
            eventArgs.Handled = true;
        };
        content.MouseEnter += (_, _) => _myDrPopupCloseTimer.Stop();
        content.MouseLeave += (_, _) => ScheduleMyDrDoctorTurnoverPopupClose();
        content.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            source.Focus();
            HideMyDrDoctorTurnoverPopup();
            eventArgs.Handled = true;
        };
    }

    private void OpenMyDrDoctorTurnoverPopup()
    {
        _myDrPopupCloseTimer.Stop();
        if (_myDrDoctorTurnoverPopup is not null) _myDrDoctorTurnoverPopup.IsOpen = true;
    }

    private void ScheduleMyDrDoctorTurnoverPopupClose()
    {
        _myDrPopupCloseTimer.Stop();
        _myDrPopupCloseTimer.Start();
    }

    private bool IsMyDrDoctorTurnoverPopupActive() =>
        _myDrDoctorTurnoverSource is { IsMouseOver: true } or { IsKeyboardFocusWithin: true } ||
        _myDrDoctorTurnoverPopup?.Child is FrameworkElement { IsMouseOver: true } or { IsKeyboardFocusWithin: true };

    private void HideMyDrDoctorTurnoverPopup()
    {
        _myDrPopupCloseTimer.Stop();
        if (_myDrDoctorTurnoverPopup is not null) _myDrDoctorTurnoverPopup.IsOpen = false;
    }

    private void CloseMyDrDoctorTurnoverPopup()
    {
        _myDrPopupCloseTimer.Stop();
        if (_myDrDoctorTurnoverPopup is not null)
        {
            _myDrDoctorTurnoverPopup.IsOpen = false;
            _myDrDoctorTurnoverPopup.Child = null;
            _myDrDoctorTurnoverPopup.PlacementTarget = null;
        }
        _myDrDoctorTurnoverPopup = null;
        _myDrDoctorTurnoverSource = null;
    }

    private static TextBlock CreateMyDrTooltipMessage(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(70, 81, 94)),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 350
    };

    private static void AddMyDrTooltipCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
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
        if (WindowState != WindowState.Minimized) { _lastVisibleState = WindowState; return; }
        HideMyDrDoctorTurnoverPopup();
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
        SaveWindowPlacement();
        HideMyDrDoctorTurnoverPopup();
        Hide();
        if (!_minimizeHintShown)
        {
            _minimizeHintShown = true;
            _notifyIcon.ShowBalloonTip(4000, "KSeF Monitor", "Okno zostało schowane. Otworzysz je ikoną KSeF obok zegara; opcja Wyjdź kończy działanie.", Forms.ToolTipIcon.Info);
        }
    }

    internal void ShowFromTray()
    {
        Show();
        WindowState = _lastVisibleState;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void PrepareForExit()
    {
        if (_reallyClose) return;
        _reallyClose = true;
        SaveWindowPlacement();
        _clock.Stop();
        CloseMyDrDoctorTurnoverPopup();
        _myDrPopupCloseTimer.Stop();
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
