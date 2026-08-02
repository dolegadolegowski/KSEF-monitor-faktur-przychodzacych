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
    private readonly DataGrid _grid = new();
    private readonly TextBlock _monthLabel = new();
    private readonly TextBlock _monthSummary = new();
    private readonly TextBlock _status = new();
    private readonly Button _previousMonth = new();
    private readonly Button _nextMonth = new();
    private readonly Button _refreshButton = new();
    private readonly System.Drawing.Icon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StatusBannerState _statusBanner = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, InvoiceDetailsWindow> _detailsWindows = new(StringComparer.Ordinal);
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _reallyClose;
    private bool _minimizeHintShown;
    private string? _notificationTarget;

    public MainWindow(AppStore store, SynchronizationService synchronization)
    {
        _store = store;
        _synchronization = synchronization;
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

        _monthSummary.HorizontalAlignment = HorizontalAlignment.Stretch;
        _monthSummary.TextAlignment = TextAlignment.Center;
        _monthSummary.VerticalAlignment = VerticalAlignment.Center;
        _monthSummary.Margin = new Thickness(18, 0, 18, 0);
        _monthSummary.Foreground = new SolidColorBrush(Color.FromRgb(70, 81, 94));
        _monthSummary.FontSize = 12.5;
        _monthSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_monthSummary, 1);
        headerGrid.Children.Add(_monthSummary);

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
        _grid.Columns.Add(new DataGridTextColumn { Header = "Kwota brutto", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.GrossAmount)), Width = 155, ElementStyle = amountStyle });
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
        var versionButton = new Button
        {
            Content = GetDisplayVersion(),
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Cursor = Cursors.Hand,
            ToolTip = "Otwórz ustawienia aplikacji"
        };
        versionButton.Click += (_, _) => OpenSettings();
        Grid.SetColumn(versionButton, 1);
        footerGrid.Children.Add(versionButton);
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
        if (_store.ConsumeLoadWarning() is not null)
            ShowStatus(new AppStatusMessage(
                "Nie udało się wczytać części zapisanych danych. Aplikacja użyła bezpiecznej kopii lub ustawień domyślnych.",
                StatusSeverity.Error));
        if (!_synchronization.IsConfigured)
            Dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.ApplicationIdle);
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
        var window = new SettingsWindow(_store) { Owner = this };
        window.ShowDialog();
        if (!window.ConfigurationChanged) return;
        ShowStatus(new AppStatusMessage("Ustawienia zapisane. Oczekiwanie na synchronizację…"));
        _synchronization.UpdateConfiguration();
        RefreshRows();
    }

    private void ChangeMonth(int offset)
    {
        var candidate = _displayMonth.AddMonths(offset);
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var oldest = current.AddMonths(-SynchronizationService.VisibleHistoryMonthsBack);
        if (candidate < oldest || candidate > current) return;
        _displayMonth = candidate;
        RefreshRows();
    }

    private void RefreshRows()
    {
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
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
        _monthSummary.Inlines.Clear();
        _monthSummary.Inlines.Add(new Run($"{invoiceCounts}  •  ") { FontSize = 12.5 });
        _monthSummary.Inlines.Add(new Run(grossTotals)
        {
            FontSize = _monthLabel.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(43, 55, 70))
        });
        _monthSummary.ToolTip = $"{invoiceCounts}  •  {grossTotals}\nKwoty w różnych walutach są sumowane oddzielnie.";
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
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "v?" : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
        _trayIcon.Dispose();
        Close();
    }
}
