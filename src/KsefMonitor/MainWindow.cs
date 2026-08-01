using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KsefMonitor;

internal sealed class MainWindow : Window
{
    private readonly AppStore _store;
    private readonly SynchronizationService _synchronization;
    private readonly ObservableCollection<InvoiceRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _monthLabel = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _lastSync = new();
    private readonly TextBlock _nextSync = new();
    private readonly Button _previousMonth = new();
    private readonly Button _nextMonth = new();
    private readonly Button _refreshButton = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _reallyClose;
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

        _notifyIcon = BuildNotifyIcon();
        _synchronization.StatusChanged += OnStatusChanged;
        _synchronization.StateChanged += OnStateChanged;
        _synchronization.NewInvoicesDiscovered += OnNewInvoicesDiscovered;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        _clock.Tick += (_, _) => UpdateSyncLabels();
        _clock.Start();
        RefreshRows();
    }

    private UIElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 229, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 18, 24, 18)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Faktury otrzymane",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(27, 37, 51))
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Krajowy System e-Faktur",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 3, 0, 0)
        });
        headerGrid.Children.Add(titleStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _refreshButton.Content = "Odśwież teraz";
        _refreshButton.Padding = new Thickness(16, 8, 16, 8);
        _refreshButton.Margin = new Thickness(0, 0, 10, 0);
        _refreshButton.Click += async (_, _) => await RefreshManuallyAsync();
        actions.Children.Add(_refreshButton);
        var settingsButton = new Button { Content = "Ustawienia", Padding = new Thickness(16, 8, 16, 8) };
        settingsButton.Click += (_, _) => OpenSettings();
        actions.Children.Add(settingsButton);
        Grid.SetColumn(actions, 1);
        headerGrid.Children.Add(actions);
        header.Child = headerGrid;
        root.Children.Add(header);

        var navigator = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251)),
            Padding = new Thickness(24, 14, 24, 10)
        };
        var navGrid = new Grid();
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var monthButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _previousMonth.Content = "←";
        _previousMonth.ToolTip = "Poprzedni miesiąc";
        _previousMonth.Width = 38;
        _previousMonth.Height = 34;
        _previousMonth.Click += (_, _) => ChangeMonth(-1);
        monthButtons.Children.Add(_previousMonth);
        _monthLabel.FontSize = 19;
        _monthLabel.FontWeight = FontWeights.SemiBold;
        _monthLabel.VerticalAlignment = VerticalAlignment.Center;
        _monthLabel.Margin = new Thickness(14, 0, 14, 0);
        monthButtons.Children.Add(_monthLabel);
        _nextMonth.Content = "→";
        _nextMonth.ToolTip = "Następny miesiąc";
        _nextMonth.Width = 38;
        _nextMonth.Height = 34;
        _nextMonth.Click += (_, _) => ChangeMonth(1);
        monthButtons.Children.Add(_nextMonth);
        navGrid.Children.Add(monthButtons);

        var syncStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _lastSync.Foreground = Brushes.DimGray;
        _nextSync.Foreground = Brushes.DimGray;
        _nextSync.FontSize = 12;
        syncStack.Children.Add(_lastSync);
        syncStack.Children.Add(_nextSync);
        Grid.SetColumn(syncStack, 2);
        navGrid.Children.Add(syncStack);
        navigator.Child = navGrid;
        Grid.SetRow(navigator, 1);
        root.Children.Add(navigator);

        _grid.ItemsSource = _rows;
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.RowHeight = 44;
        _grid.Margin = new Thickness(24, 0, 24, 14);
        _grid.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 232));
        _grid.Columns.Add(new DataGridTextColumn { Header = string.Empty, Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.NewLabel)), Width = 68 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Data wystawienia", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.IssueDate)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Sprzedawca", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.Seller)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Numer faktury", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.InvoiceNumber)), Width = 180 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Kwota brutto", Binding = new System.Windows.Data.Binding(nameof(InvoiceRow.GrossAmount)), Width = 155 });
        _grid.PreviewMouseLeftButtonUp += OnGridMouseLeftButtonUp;
        Grid.SetRow(_grid, 2);
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
        footer.Child = _status;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private Forms.NotifyIcon BuildNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Otwórz", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Odśwież", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshManuallyAsync()));
        menu.Items.Add("Ustawienia", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Wyjdź", null, (_, _) => Dispatcher.Invoke(() => ((App)System.Windows.Application.Current).ExitApplication()));

        var icon = new Forms.NotifyIcon
        {
            Text = "KSeF Monitor",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        icon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            if (_notificationTarget is not null && _synchronization.State.Invoices.TryGetValue(_notificationTarget, out var target))
                OpenInvoice(target);
        });
        return icon;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _synchronization.Start();
        if (!_store.LoadSettings().IsConfigured || string.IsNullOrWhiteSpace(_store.LoadToken()))
            Dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.ApplicationIdle);
    }

    private async System.Threading.Tasks.Task RefreshManuallyAsync()
    {
        _refreshButton.IsEnabled = false;
        try
        {
            await _synchronization.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Nie udało się odświeżyć", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _status.Text = "Ustawienia zapisane. Oczekiwanie na synchronizację…";
        _synchronization.UpdateConfiguration();
        RefreshRows();
    }

    private void ChangeMonth(int offset)
    {
        var candidate = _displayMonth.AddMonths(offset);
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var oldest = current.AddMonths(-1);
        if (candidate < oldest || candidate > current) return;
        _displayMonth = candidate;
        RefreshRows();
    }

    private void RefreshRows()
    {
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var oldest = current.AddMonths(-1);
        if (_displayMonth < oldest || _displayMonth > current) _displayMonth = current;

        var culture = CultureInfo.GetCultureInfo("pl-PL");
        _monthLabel.Text = culture.TextInfo.ToTitleCase(_displayMonth.ToString("MMMM yyyy", culture));
        _previousMonth.IsEnabled = _displayMonth > oldest;
        _nextMonth.IsEnabled = _displayMonth < current;

        var items = _synchronization.State.Invoices.Values
            .Where(x => x.IssueDate.Year == _displayMonth.Year && x.IssueDate.Month == _displayMonth.Month)
            .OrderByDescending(x => x.IsNew)
            .ThenByDescending(x => x.IssueDate)
            .ThenByDescending(x => x.PermanentStorageDate)
            .Select(x => new InvoiceRow { Source = x })
            .ToList();

        _rows.Clear();
        foreach (var item in items) _rows.Add(item);
        UpdateSyncLabels();
    }

    private void UpdateSyncLabels()
    {
        _lastSync.Text = _synchronization.State.LastSuccessfulSyncUtc is { } last
            ? $"Ostatnie odświeżenie: {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Jeszcze nie odświeżono";

        if (_synchronization.NextScheduledSyncUtc is not { } next)
        {
            _nextSync.Text = string.Empty;
            return;
        }

        var remaining = next - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        _nextSync.Text = $"Następna próba za {Math.Ceiling(remaining.TotalMinutes):0} min";
    }

    private void OnGridMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is not DataGridRow row) return;
        if (row.Item is InvoiceRow invoiceRow) OpenInvoice(invoiceRow.Source);
    }

    private void OpenInvoice(StoredInvoice invoice)
    {
        var details = new InvoiceDetailsWindow(invoice) { Owner = this };
        _synchronization.MarkViewed(invoice.KsefNumber);
        details.Show();
    }

    private void OnStatusChanged(object? sender, string text) => Dispatcher.InvokeAsync(() => _status.Text = text);

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _refreshButton.IsEnabled = !_synchronization.IsSynchronizing;
        RefreshRows();
    });

    private void OnNewInvoicesDiscovered(object? sender, IReadOnlyList<StoredInvoice> invoices) => Dispatcher.InvokeAsync(() =>
    {
        var settings = _store.LoadSettings();
        if (!settings.NotificationsEnabled) return;
        var notNotified = invoices.Where(x => x.NotifiedAtUtc is null).ToList();
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
        _notifyIcon.ShowBalloonTip(2500, "KSeF Monitor", "Aplikacja nadal działa i sprawdza faktury co 15 minut.", Forms.ToolTipIcon.Info);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
    }

    private void ShowFromTray()
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
        _reallyClose = true;
        _clock.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Close();
    }
}
