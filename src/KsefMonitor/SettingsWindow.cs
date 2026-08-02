using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operacje użytkownika pokazują każdy błąd połączenia lub zapisu w oknie ustawień.")]
internal sealed class SettingsWindow : Window
{
    private readonly AppStore _store;
    private readonly TextBox _nip = new();
    private readonly PasswordBox _token = new();
    private readonly CheckBox _notifications = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _logText = new();
    private readonly TextBlock _logStatus = new();
    private readonly Button _testButton = new();
    private readonly Button _saveButton = new();
    private TabItem? _logTab;
    private bool _requiresProductionToken;
    private string _originalNip = string.Empty;
    private CancellationTokenSource? _testCancellation;
    private bool _isClosing;

    public SettingsWindow(AppStore store)
    {
        _store = store;
        Title = "Ustawienia KSeF";
        Width = 700;
        Height = 580;
        MinWidth = 620;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Icon = TrayIconFactory.CreateImageSource();
        Content = BuildContent();
        LoadCurrentSettings();
        Closing += (_, _) =>
        {
            _isClosing = true;
            _testCancellation?.Cancel();
        };
    }

    public bool ConfigurationChanged { get; private set; }

    private TabControl BuildContent()
    {
        var tabs = new TabControl { Margin = new Thickness(14) };
        tabs.Items.Add(new TabItem { Header = "Połączenie", Content = BuildConnectionContent() });
        _logTab = new TabItem { Header = "Dziennik", Content = BuildLogContent() };
        tabs.Items.Add(_logTab);
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, tabs) && ReferenceEquals(tabs.SelectedItem, _logTab)) RefreshLog();
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
            Text = "Zawiera informacje techniczne pomocne przy diagnozowaniu problemów. Dziennik nie zapisuje tokena KSeF ani treści XML faktur.",
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
            using var client = new KsefApiClient(ReadSettings(), token);
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
}
