using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KsefMonitor;

internal sealed class InvoiceDetailsWindow : Window
{
    private readonly StoredInvoice _invoice;

    public InvoiceDetailsWindow(StoredInvoice invoice)
    {
        _invoice = invoice;
        Title = $"Faktura {invoice.InvoiceNumber}";
        Width = 980;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(246, 248, 251)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

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
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_invoice.SellerName) ? _invoice.SellerNip : _invoice.SellerName,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = $"Faktura {_invoice.InvoiceNumber}  •  {_invoice.IssueDate:dd.MM.yyyy}",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0)
        });
        headerGrid.Children.Add(text);
        var amount = new TextBlock
        {
            Text = $"{_invoice.GrossAmount:N2} {_invoice.Currency}",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(amount, 1);
        headerGrid.Children.Add(amount);
        header.Child = headerGrid;
        root.Children.Add(header);

        var tabs = new TabControl { Margin = new Thickness(20) };
        var parsed = TryParseXml();
        tabs.Items.Add(new TabItem { Header = "Podsumowanie", Content = BuildSummary(parsed) });
        tabs.Items.Add(new TabItem { Header = $"Pozycje ({parsed?.Lines.Count ?? 0})", Content = BuildLines(parsed) });
        tabs.Items.Add(new TabItem { Header = "Wszystkie pola", Content = BuildFields(parsed) });
        tabs.Items.Add(new TabItem { Header = "XML", Content = BuildXml() });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        return root;
    }

    private InvoiceDocument? TryParseXml()
    {
        if (string.IsNullOrWhiteSpace(_invoice.Xml)) return null;
        try { return InvoiceXmlReader.Parse(_invoice.Xml); }
        catch { return null; }
    }

    private UIElement BuildSummary(InvoiceDocument? document)
    {
        var fields = new List<InvoiceField>
        {
            new("Numer KSeF", _invoice.KsefNumber),
            new("Numer faktury", _invoice.InvoiceNumber),
            new("Data wystawienia", _invoice.IssueDate.ToString("dd.MM.yyyy")),
            new("Sprzedawca", _invoice.SellerName),
            new("NIP sprzedawcy", _invoice.SellerNip),
            new("Nabywca", _invoice.BuyerName),
            new("Identyfikator nabywcy", _invoice.BuyerIdentifier),
            new("Kwota netto", $"{_invoice.NetAmount:N2} {_invoice.Currency}"),
            new("VAT", $"{_invoice.VatAmount:N2} PLN"),
            new("Kwota brutto", $"{_invoice.GrossAmount:N2} {_invoice.Currency}"),
            new("Typ faktury", _invoice.InvoiceType),
            new("Schemat", _invoice.FormCode),
            new("Tryb fakturowania", _invoice.InvoicingMode),
            new("Data nadania numeru KSeF", FormatDate(_invoice.AcquisitionDate)),
            new("Data trwałego zapisu", FormatDate(_invoice.PermanentStorageDate)),
            new("Załącznik", _invoice.HasAttachment ? "Tak" : "Nie"),
            new("Samofakturowanie", _invoice.IsSelfInvoicing ? "Tak" : "Nie")
        };

        if (document is not null)
        {
            foreach (var item in document.Summary)
                if (!fields.Any(x => string.Equals(x.Path, item.Key, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(new InvoiceField(item.Key, item.Value));
        }

        var panel = new DockPanel();
        if (document is null)
        {
            var notice = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 247, 224)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 181, 76)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "Pełny XML nie został jeszcze pobrany. Metadane są dostępne, a treść dokumentu zostanie ponowiona w następnym cyklu synchronizacji.",
                    TextWrapping = TextWrapping.Wrap
                }
            };
            DockPanel.SetDock(notice, Dock.Top);
            panel.Children.Add(notice);
        }
        panel.Children.Add(CreateFieldGrid(fields));
        return panel;
    }

    private static UIElement BuildLines(InvoiceDocument? document)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            ItemsSource = document?.Lines ?? new List<InvoiceLine>(),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Lp.", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.Number)), Width = 55 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Opis", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ilość", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.Quantity)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "J.m.", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.Unit)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Cena netto", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.UnitNetPrice)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Cena brutto", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.UnitGrossPrice)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Rabat", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.Discount)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Wartość netto", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.NetAmount)), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Wartość brutto", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.GrossAmount)), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Kwota VAT", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.VatAmount)), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Stawka VAT", Binding = new System.Windows.Data.Binding(nameof(InvoiceLine.VatRate)), Width = 90 });

        var panel = new DockPanel();
        var notice = new TextBlock
        {
            Text = "Puste pole oznacza, że wystawca nie przekazał tej wartości w XML faktury KSeF.",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(4, 0, 4, 10),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(notice, Dock.Top);
        panel.Children.Add(notice);
        panel.Children.Add(grid);
        return panel;
    }

    private static UIElement BuildFields(InvoiceDocument? document) =>
        CreateFieldGrid(document?.Fields ?? new List<InvoiceField>());

    private UIElement BuildXml() => new TextBox
    {
        Text = _invoice.Xml ?? "XML nie został jeszcze pobrany.",
        IsReadOnly = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12
    };

    private static DataGrid CreateFieldGrid(IEnumerable<InvoiceField> fields)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            ItemsSource = fields,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Pole", Binding = new System.Windows.Data.Binding(nameof(InvoiceField.Path)), Width = new DataGridLength(0.42, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Wartość", Binding = new System.Windows.Data.Binding(nameof(InvoiceField.Value)), Width = new DataGridLength(0.58, DataGridLengthUnitType.Star) });
        return grid;
    }

    private static string FormatDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("pl-PL")) ?? string.Empty;
}
