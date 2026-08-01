using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KsefMonitor;

internal sealed class SettingsWindow : Window
{
    private readonly AppStore _store;
    private readonly ComboBox _environment = new();
    private readonly TextBox _nip = new();
    private readonly PasswordBox _token = new();
    private readonly CheckBox _notifications = new();
    private readonly TextBlock _status = new();
    private readonly Button _testButton = new();
    private readonly Button _saveButton = new();
    private KsefEnvironment _originalEnvironment;

    public SettingsWindow(AppStore store)
    {
        _store = store;
        Title = "Ustawienia KSeF";
        Width = 560;
        Height = 520;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Content = BuildContent();
        LoadCurrentSettings();
    }

    public bool ConfigurationChanged { get; private set; }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(28) };
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
            Text = "Token powinien mieć wyłącznie uprawnienie InvoiceRead.",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 5, 0, 20)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 7; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddLabel(form, "Środowisko", 0);
        _environment.Items.Add("TEST");
        _environment.Items.Add("DEMO");
        _environment.Items.Add("PRODUKCJA");
        _environment.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_environment, 0);
        Grid.SetColumn(_environment, 1);
        form.Children.Add(_environment);

        AddLabel(form, "NIP firmy", 1);
        _nip.Margin = new Thickness(0, 0, 0, 14);
        _nip.MaxLength = 13;
        Grid.SetRow(_nip, 1);
        Grid.SetColumn(_nip, 1);
        form.Children.Add(_nip);

        AddLabel(form, "Token KSeF", 2);
        _token.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(_token, 2);
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
        Grid.SetRow(tokenHint, 3);
        Grid.SetColumn(tokenHint, 1);
        form.Children.Add(tokenHint);

        _notifications.Content = "Pokazuj powiadomienia o nowych fakturach";
        _notifications.Margin = new Thickness(0, 0, 0, 18);
        Grid.SetRow(_notifications, 4);
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
                Text = "Najpierw przetestuj integrację w środowisku TEST. Nie używaj tam prawdziwych danych ani produkcyjnego tokena.",
                TextWrapping = TextWrapping.Wrap
            }
        };
        Grid.SetRow(warning, 5);
        Grid.SetColumn(warning, 0);
        Grid.SetColumnSpan(warning, 2);
        form.Children.Add(warning);

        _status.Margin = new Thickness(0, 14, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 6);
        Grid.SetColumn(_status, 0);
        Grid.SetColumnSpan(_status, 2);
        form.Children.Add(_status);

        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _testButton.Content = "Sprawdź połączenie";
        _testButton.Padding = new Thickness(14, 7, 14, 7);
        _testButton.Margin = new Thickness(0, 0, 10, 0);
        _testButton.Click += async (_, _) => await TestConnectionAsync();
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

    private void LoadCurrentSettings()
    {
        var settings = _store.LoadSettings();
        _originalEnvironment = settings.Environment;
        _environment.SelectedIndex = settings.Environment switch
        {
            KsefEnvironment.Test => 0,
            KsefEnvironment.Demo => 1,
            _ => 2
        };
        _nip.Text = settings.Nip;
        _notifications.IsChecked = settings.NotificationsEnabled;
    }

    private AppSettings ReadSettings() => new()
    {
        Environment = _environment.SelectedIndex switch
        {
            0 => KsefEnvironment.Test,
            1 => KsefEnvironment.Demo,
            _ => KsefEnvironment.Production
        },
        Nip = NipValidator.Normalize(_nip.Text),
        NotificationsEnabled = _notifications.IsChecked == true
    };

    private string? Validate(bool requireEnteredToken)
    {
        if (!NipValidator.IsValid(_nip.Text)) return "Wprowadź poprawny polski NIP wraz z cyfrą kontrolną.";
        if (ReadSettings().Environment != _originalEnvironment && string.IsNullOrWhiteSpace(_token.Password))
            return "Po zmianie środowiska wprowadź token właściwy dla nowego środowiska.";
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
        try
        {
            using var client = new KsefApiClient(ReadSettings(), token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await client.AuthenticateAsync(timeout.Token);
            SetStatus("Połączenie działa. Token ma aktywne uprawnienia do wskazanego kontekstu.", isError: false);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
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

        _store.SaveSettings(ReadSettings());
        if (!string.IsNullOrWhiteSpace(_token.Password)) _store.SaveToken(_token.Password);
        ConfigurationChanged = true;
        DialogResult = true;
        Close();
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
