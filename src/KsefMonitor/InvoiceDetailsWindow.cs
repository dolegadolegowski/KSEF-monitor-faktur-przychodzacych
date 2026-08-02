using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Błędy dokumentu i renderowania są prezentowane wewnątrz okna, aby pojedyncza faktura nie zamknęła aplikacji.")]
internal sealed class InvoiceDetailsWindow : Window
{
    private const double A4Width = 794;
    private const double A4Height = 1123;
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r"];
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(29, 39, 52));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(92, 103, 116));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(20, 73, 122));
    private static readonly Brush Rule = new SolidColorBrush(Color.FromRgb(211, 217, 225));
    private static readonly Brush SoftBackground = new SolidColorBrush(Color.FromRgb(244, 247, 250));

    private StoredInvoice _invoice;
    private readonly ApplicationLog _log;
    private readonly Func<string, CancellationToken, Task<StoredInvoice?>> _loadInvoice;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly TabControl _tabs = new();
    private bool _isClosed;
    private bool _isLoading;
    private bool _contentDisplayed;

    public event EventHandler? InvoiceContentDisplayed;

    public InvoiceDetailsWindow(
        StoredInvoice invoice,
        ApplicationLog log,
        Func<string, CancellationToken, Task<StoredInvoice?>> loadInvoice)
    {
        _invoice = invoice;
        _log = log;
        _loadInvoice = loadInvoice;
        Title = $"Faktura {invoice.InvoiceNumber}";
        Width = 1120;
        Height = 840;
        MinWidth = 900;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(235, 239, 244));
        Icon = TrayIconFactory.CreateImageSource();
        Content = BuildContent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _loadCancellation.Cancel();
        };
    }

    private Grid BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = Brushes.White,
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 15, 24, 15)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = GetDocumentTitle(),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{_invoice.InvoiceNumber}  •  {(_invoice.IssueDate == DateOnly.MinValue ? "—" : _invoice.IssueDate.ToString("dd.MM.yyyy", PolishCulture))}  •  {_invoice.KsefNumber}",
            Foreground = Muted,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        headerGrid.Children.Add(title);

        var amount = new TextBlock
        {
            Text = $"{_invoice.GrossAmount:N2} {_invoice.Currency}",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 0, 0)
        };
        Grid.SetColumn(amount, 1);
        headerGrid.Children.Add(amount);
        header.Child = headerGrid;
        root.Children.Add(header);

        _tabs.Margin = new Thickness(16, 12, 16, 16);
        _tabs.SelectionChanged += OnTabSelectionChanged;
        _tabs.Items.Add(new TabItem { Header = "Dokument A4", Content = BuildLoadingNotice("Przygotowywanie podglądu faktury…") });
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);
        return root;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadInvoiceAsync().ConfigureAwait(true);
    }

    private async Task LoadInvoiceAsync()
    {
        if (_isLoading || _isClosed) return;
        _isLoading = true;
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem
        {
            Header = "Dokument A4",
            Content = BuildLoadingNotice(string.IsNullOrWhiteSpace(_invoice.Xml)
                ? "Pobieranie pełnej treści faktury z KSeF…"
                : "Przygotowywanie podglądu faktury…")
        });

        InvoiceDocument? document = null;
        string? previewError = null;
        var canRetry = false;
        try
        {
            if (string.IsNullOrWhiteSpace(_invoice.Xml))
            {
                var refreshed = await _loadInvoice(_invoice.KsefNumber, _loadCancellation.Token).ConfigureAwait(true);
                if (refreshed is not null) _invoice = refreshed;
            }

            if (string.IsNullOrWhiteSpace(_invoice.Xml))
            {
                previewError = "Pełna treść faktury nie jest jeszcze dostępna. Kliknij „Spróbuj ponownie”.";
                canRetry = true;
            }
            else
            {
                document = await Task.Run(() => InvoiceXmlReader.Parse(_invoice.Xml), _loadCancellation.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_isClosed || _loadCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (InvoiceContentPendingException exception)
        {
            previewError = $"KSeF ogranicza liczbę pobrań. Pełną treść będzie można ponownie pobrać po {exception.RetryAtUtc.ToLocalTime():HH:mm}.";
            canRetry = true;
        }
        catch (KsefApiException exception)
        {
            previewError = FormatDownloadError(exception);
            canRetry = true;
        }
        catch (HttpRequestException)
        {
            previewError = "Nie udało się połączyć z KSeF. Sprawdź internet i spróbuj ponownie.";
            canRetry = true;
        }
        catch (OperationCanceledException)
        {
            previewError = "Pobieranie zostało przerwane. Spróbuj ponownie.";
            canRetry = true;
        }
        catch (Exception exception)
        {
            _log.Error("Podgląd faktury", "Nie udało się odczytać XML faktury.", exception);
            previewError = string.IsNullOrWhiteSpace(_invoice.Xml)
                ? "Nie udało się pobrać pełnych danych faktury. Spróbuj ponownie."
                : "Nie udało się odczytać szczegółów tej faktury. Szczegóły zapisano w dzienniku aplikacji.";
            canRetry = string.IsNullOrWhiteSpace(_invoice.Xml);
        }
        finally
        {
            _isLoading = false;
        }

        if (_isClosed) return;
        try
        {
            PopulateTabs(document, previewError, canRetry);
            if (document is not null && !_contentDisplayed)
            {
                _contentDisplayed = true;
                InvoiceContentDisplayed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            _log.Error("Podgląd faktury", "Nie udało się zbudować podglądu A4.", exception);
            _tabs.Items.Clear();
            _tabs.Items.Add(new TabItem
            {
                Header = "Dokument A4",
                Content = BuildLoadingNotice("Nie udało się zbudować podglądu faktury. Szczegóły zapisano w dzienniku aplikacji.", isError: true)
            });
        }
    }

    private void PopulateTabs(InvoiceDocument? document, string? previewError, bool canRetry)
    {
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem { Header = "Dokument A4", Content = BuildA4Preview(document, previewError, canRetry) });
        _tabs.Items.Add(CreateLazyTab("Dane KSeF", () => BuildSummary(document)));
        _tabs.Items.Add(CreateLazyTab("Wszystkie pola XML", () => BuildFields(document)));
        _tabs.Items.Add(CreateLazyTab("Surowy XML", BuildXml));
        _tabs.SelectedIndex = 0;
    }

    private static TabItem CreateLazyTab(string header, Func<UIElement> contentFactory) => new()
    {
        Header = header,
        Tag = contentFactory,
        Content = BuildLoadingNotice("Otwórz zakładkę, aby załadować jej zawartość.")
    };

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _tabs) || _tabs.SelectedItem is not TabItem { Tag: Func<UIElement> factory } tab) return;
        tab.Tag = null;
        try
        {
            tab.Content = factory();
        }
        catch (Exception exception)
        {
            _log.Error("Podgląd faktury", "Nie udało się załadować zakładki szczegółów faktury.", exception);
            tab.Content = BuildLoadingNotice("Nie udało się załadować tej zakładki. Szczegóły zapisano w dzienniku aplikacji.", isError: true);
        }
    }

    private static Border BuildLoadingNotice(string text, bool isError = false) => new()
    {
        Margin = new Thickness(24),
        Padding = new Thickness(18),
        Background = isError ? new SolidColorBrush(Color.FromRgb(255, 238, 238)) : Brushes.White,
        BorderBrush = isError ? Brushes.IndianRed : Rule,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Child = new TextBlock
        {
            Text = text,
            Foreground = isError ? Brushes.Firebrick : Ink,
            TextWrapping = TextWrapping.Wrap
        }
    };

    private static string FormatDownloadError(KsefApiException exception)
    {
        if (exception.HasErrorCode(21165))
            return "KSeF nie udostępnił jeszcze pełnej treści tej faktury. Odczekaj chwilę i spróbuj ponownie.";
        return exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Token nie pozwala pobrać pełnej treści faktury. Otwórz ustawienia i sprawdź token.",
            HttpStatusCode.TooManyRequests =>
                "KSeF chwilowo ograniczył liczbę pobrań. Odczekaj kilka minut i spróbuj ponownie.",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                "KSeF jest chwilowo niedostępny. Spróbuj ponownie za kilka minut.",
            _ => "Nie udało się pobrać pełnych danych faktury. Spróbuj ponownie."
        };
    }

    private ScrollViewer BuildA4Preview(InvoiceDocument? document, string? previewError, bool canRetry)
    {
        var host = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24)
        };

        if (!string.IsNullOrWhiteSpace(previewError))
            host.Children.Add(BuildPreviewNotice(previewError, isError: true, canRetry));

        var lines = (IReadOnlyList<InvoiceLine>?)document?.Lines ?? Array.Empty<InvoiceLine>();
        var pages = InvoicePagePlanner.Plan(lines, EstimateFirstPageHeaderExtraHeight(document));
        var hasNetUnitPrice = lines.Any(x => !string.IsNullOrWhiteSpace(x.UnitNetPrice));
        var hasGrossUnitPrice = lines.Any(x => !string.IsNullOrWhiteSpace(x.UnitGrossPrice));
        foreach (var page in pages)
            host.Children.Add(BuildA4Page(document, page, hasNetUnitPrice, hasGrossUnitPrice));

        return new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(225, 230, 236)),
            CanContentScroll = false
        };
    }

    private Border BuildA4Page(
        InvoiceDocument? document,
        InvoicePagePlan page,
        bool hasNetUnitPrice,
        bool hasGrossUnitPrice)
    {
        var pageBorder = new Border
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White,
            Padding = new Thickness(42),
            Margin = new Thickness(0, 0, 0, 24),
            Effect = page.PageCount <= 12
                ? new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = 0.16
                }
                : null
        };

        var pageGrid = new Grid();
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(page.IsFirst ? BuildDocumentHeader(document) : BuildContinuationHeader(page));

        if (page.Lines.Count > 0)
            content.Children.Add(BuildLineTable(page.Lines, hasNetUnitPrice, hasGrossUnitPrice));
        else if (document is null)
            content.Children.Add(BuildInlineNotice("Pełny XML faktury nie został jeszcze pobrany. Dokument pokazuje obecnie metadane KSeF."));
        else
            content.Children.Add(BuildInlineNotice("XML faktury nie zawiera szczegółowych pozycji dokumentu."));

        if (page.IsLast)
            content.Children.Add(BuildTotalsAndPayment(document));

        pageGrid.Children.Add(content);
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hasCalculatedLineAmounts = page.Lines.Any(line =>
            line.IsVatAmountCalculated || line.IsGrossAmountCalculated);
        footer.Children.Add(new TextBlock
        {
            Text = hasCalculatedLineAmounts
                ? "Wizualizacja KSeF • * kwota pozycji wyliczona na potrzeby podglądu"
                : "Wizualizacja danych faktury ustrukturyzowanej KSeF",
            FontSize = 8.5,
            Foreground = Muted
        });
        var pageNumber = new TextBlock
        {
            Text = $"Strona {page.PageNumber} z {page.PageCount}",
            FontSize = 8.5,
            Foreground = Muted
        };
        Grid.SetColumn(pageNumber, 1);
        footer.Children.Add(pageNumber);
        Grid.SetRow(footer, 1);
        pageGrid.Children.Add(footer);
        pageBorder.Child = pageGrid;
        return pageBorder;
    }

    private StackPanel BuildDocumentHeader(InvoiceDocument? document)
    {
        var panel = new StackPanel();
        var top = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        brand.Children.Add(new Border
        {
            Background = Accent,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = "KSeF",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14
            }
        });
        brand.Children.Add(new TextBlock
        {
            Text = "Faktura ustrukturyzowana",
            Foreground = Muted,
            FontSize = 9.5,
            Margin = new Thickness(0, 6, 0, 0)
        });
        top.Children.Add(brand);

        var documentInfo = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        documentInfo.Children.Add(new TextBlock
        {
            Text = GetDocumentTitle().ToUpper(PolishCulture),
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Right
        });
        documentInfo.Children.Add(new TextBlock
        {
            Text = InvoiceValueFormatter.TextOrDash(_invoice.InvoiceNumber),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 8)
        });
        documentInfo.Children.Add(BuildHeaderMetaRow("Data wystawienia", _invoice.IssueDate == DateOnly.MinValue ? "—" : _invoice.IssueDate.ToString("dd.MM.yyyy", PolishCulture)));
        var saleDate = GetSummary(document, "Data sprzedaży");
        if (!string.IsNullOrWhiteSpace(saleDate))
            documentInfo.Children.Add(BuildHeaderMetaRow("Data sprzedaży", FormatDateText(saleDate)));
        top.Children.Add(documentInfo);
        Grid.SetColumn(documentInfo, 1);
        panel.Children.Add(top);

        var parties = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        parties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parties.Children.Add(BuildPartyBlock(
            "SPRZEDAWCA",
            GetSummary(document, "Sprzedawca", _invoice.SellerName),
            "NIP",
            GetSummary(document, "NIP sprzedawcy", _invoice.SellerNip),
            GetSummary(document, "Adres sprzedawcy")));
        var buyer = BuildPartyBlock(
            "NABYWCA",
            GetSummary(document, "Nabywca", _invoice.BuyerName),
            "Identyfikator",
            GetSummary(document, "NIP nabywcy", _invoice.BuyerIdentifier),
            GetSummary(document, "Adres nabywcy"));
        Grid.SetColumn(buyer, 2);
        parties.Children.Add(buyer);
        panel.Children.Add(parties);
        return panel;
    }

    private Border BuildContinuationHeader(InvoicePagePlan page)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = $"{GetDocumentTitle()} {_invoice.InvoiceNumber}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_invoice.SellerName) ? _invoice.SellerNip : _invoice.SellerName,
            FontSize = 9.5,
            Foreground = Muted,
            Margin = new Thickness(0, 2, 0, 0)
        });
        grid.Children.Add(left);
        var label = new TextBlock
        {
            Text = $"ciąg dalszy • strona {page.PageNumber}/{page.PageCount}",
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return new Border
        {
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 10),
            Child = grid
        };
    }

    private static StackPanel BuildHeaderMetaRow(string label, string value)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(new TextBlock { Text = $"{label}: ", Foreground = Muted, FontSize = 9.5 });
        panel.Children.Add(new TextBlock { Text = value, Foreground = Ink, FontWeight = FontWeights.SemiBold, FontSize = 9.5 });
        return panel;
    }

    private static Border BuildPartyBlock(string heading, string name, string identifierLabel, string identifier, string address)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Accent,
            Margin = new Thickness(0, 0, 0, 7)
        });
        content.Children.Add(new TextBlock
        {
            Text = InvoiceValueFormatter.TextOrDash(name),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{identifierLabel}: {InvoiceValueFormatter.TextOrDash(identifier)}",
            FontSize = 9.5,
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(address))
            content.Children.Add(new TextBlock
            {
                Text = address,
                FontSize = 9.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

        return new Border
        {
            Background = SoftBackground,
            BorderBrush = Rule,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            MinHeight = 120,
            Child = content
        };
    }

    private static StackPanel BuildLineTable(
        IReadOnlyList<InvoiceLine> lines,
        bool hasNetUnitPrice,
        bool hasGrossUnitPrice)
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildLineGrid(null, isHeader: true, 32, hasNetUnitPrice, hasGrossUnitPrice));
        foreach (var line in lines)
            panel.Children.Add(BuildLineGrid(line, isHeader: false, InvoicePagePlanner.EstimateLineHeight(line), hasNetUnitPrice, hasGrossUnitPrice));
        return panel;
    }

    private static Grid BuildLineGrid(
        InvoiceLine? line,
        bool isHeader,
        double height,
        bool hasNetUnitPrice,
        bool hasGrossUnitPrice)
    {
        var grid = new Grid { Height = height, Background = isHeader ? Accent : Brushes.White, ClipToBounds = true };
        AddLineColumns(grid);
        var priceHeader = hasNetUnitPrice && hasGrossUnitPrice
            ? "Cena jedn.\nN/B"
            : hasGrossUnitPrice
                ? "Cena brutto"
                : hasNetUnitPrice ? "Cena netto" : "Cena jedn.";
        var description = line?.Description ?? string.Empty;
        if (line is not null && !string.IsNullOrWhiteSpace(line.Discount))
            description += $"\nRabat/obniżka: {InvoiceValueFormatter.Money(line.Discount)}";

        var values = isHeader
            ? new[] { "Lp.", "Nazwa towaru lub usługi", "Ilość", "J.m.", priceHeader, "Wartość netto", "VAT", "Kwota VAT", "Wartość brutto" }
            : new[]
            {
                InvoiceValueFormatter.TextOrDash(line!.Number),
                InvoiceValueFormatter.TextOrDash(description),
                InvoiceValueFormatter.Quantity(line.Quantity),
                InvoiceValueFormatter.TextOrDash(line.Unit),
                FormatUnitPrice(line, hasNetUnitPrice, hasGrossUnitPrice),
                InvoiceValueFormatter.Money(line.NetAmount),
                InvoiceValueFormatter.VatRate(line.VatRate),
                FormatLineMoney(line.VatAmount, line.IsVatAmountCalculated),
                FormatLineMoney(line.GrossAmount, line.IsGrossAmountCalculated)
            };

        for (var index = 0; index < values.Length; index++)
        {
            var text = new TextBlock
            {
                Text = values[index],
                Foreground = isHeader ? Brushes.White : Ink,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = isHeader ? 8.5 : 9,
                TextWrapping = index == 1 || isHeader ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = isHeader ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextAlignment = index == 1 ? TextAlignment.Left : TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!isHeader && line is not null &&
                (index == 7 && line.IsVatAmountCalculated || index == 8 && line.IsGrossAmountCalculated))
                text.ToolTip = "Kwota wyliczona na potrzeby podglądu; podsumowanie pochodzi z danych KSeF.";
            var cell = new Border
            {
                BorderBrush = isHeader ? new SolidColorBrush(Color.FromRgb(70, 112, 153)) : Rule,
                BorderThickness = new Thickness(index == 0 ? 1 : 0, 0, 1, 1),
                Padding = new Thickness(index == 1 ? 6 : 4, 3, index == 1 ? 6 : 4, 3),
                ClipToBounds = true,
                Child = text
            };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static string FormatLineMoney(string value, bool isCalculated)
    {
        var formatted = InvoiceValueFormatter.Money(value);
        return isCalculated && formatted != "—" ? $"{formatted}*" : formatted;
    }

    private static void AddLineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(79) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
    }

    private static string FormatUnitPrice(InvoiceLine line, bool hasNetUnitPrice, bool hasGrossUnitPrice)
    {
        if (!string.IsNullOrWhiteSpace(line.UnitNetPrice))
        {
            var value = InvoiceValueFormatter.Money(line.UnitNetPrice);
            return hasNetUnitPrice && hasGrossUnitPrice ? $"{value} N" : value;
        }
        if (!string.IsNullOrWhiteSpace(line.UnitGrossPrice))
        {
            var value = InvoiceValueFormatter.Money(line.UnitGrossPrice);
            return hasNetUnitPrice && hasGrossUnitPrice ? $"{value} B" : value;
        }
        return "—";
    }

    private StackPanel BuildTotalsAndPayment(InvoiceDocument? document)
    {
        var wrapper = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });

        var payment = new StackPanel();
        payment.Children.Add(new TextBlock
        {
            Text = "PŁATNOŚĆ",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = Accent,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddPaymentRow(payment, "Forma", FormatPaymentMethod(GetSummary(document, "Forma płatności")));
        AddPaymentRow(payment, "Termin", FormatDateText(GetSummary(document, "Termin płatności")));
        AddPaymentRow(payment, "Rachunek", GetSummary(document, "Rachunek bankowy"));
        grid.Children.Add(payment);

        var totals = new StackPanel();
        totals.Children.Add(BuildTotalRow("Razem netto", $"{_invoice.NetAmount:N2} {_invoice.Currency}"));
        totals.Children.Add(BuildTotalRow("Podatek VAT", $"{_invoice.VatAmount:N2} {_invoice.Currency}"));
        totals.Children.Add(BuildTotalRow("Razem brutto", $"{_invoice.GrossAmount:N2} {_invoice.Currency}"));
        var payable = GetSummary(document, "Do zapłaty");
        totals.Children.Add(new Border
        {
            Background = Accent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 7, 0, 0),
            Child = BuildTotalRowContent(
                "DO ZAPŁATY",
                string.IsNullOrWhiteSpace(payable)
                    ? $"{_invoice.GrossAmount:N2} {_invoice.Currency}"
                    : $"{InvoiceValueFormatter.Money(payable)} {_invoice.Currency}",
                Brushes.White,
                true)
        });
        Grid.SetColumn(totals, 2);
        grid.Children.Add(totals);
        wrapper.Children.Add(grid);

        wrapper.Children.Add(new Border
        {
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = $"Numer KSeF: {_invoice.KsefNumber}",
                FontSize = 8.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap
            }
        });
        return wrapper;
    }

    private static void AddPaymentRow(Panel panel, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        row.Children.Add(new TextBlock { Text = $"{label}: ", Foreground = Muted, FontSize = 9.5 });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Ink,
            FontWeight = FontWeights.SemiBold,
            FontSize = 9.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300
        });
        panel.Children.Add(row);
    }

    private static Border BuildTotalRow(string label, string value) => new()
    {
        BorderBrush = Rule,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(4, 6, 4, 6),
        Child = BuildTotalRowContent(label, value, Ink, false)
    };

    private static Grid BuildTotalRowContent(string label, string value, Brush foreground, bool emphasize)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = foreground,
            FontSize = emphasize ? 10.5 : 9.5,
            FontWeight = emphasize ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        });
        var amount = new TextBlock
        {
            Text = value,
            Foreground = foreground,
            FontSize = emphasize ? 13 : 10,
            FontWeight = FontWeights.Bold
        };
        Grid.SetColumn(amount, 1);
        grid.Children.Add(amount);
        return grid;
    }

    private static Border BuildInlineNotice(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(255, 248, 229)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(226, 185, 89)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(14),
        Margin = new Thickness(0, 8, 0, 0),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 10 }
    };

    private Border BuildPreviewNotice(string text, bool isError, bool canRetry)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isError ? Brushes.Firebrick : Ink
        });
        if (canRetry)
        {
            var retry = new Button
            {
                Content = "Spróbuj ponownie",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 10, 0, 0)
            };
            retry.Click += async (_, _) => await LoadInvoiceAsync().ConfigureAwait(true);
            content.Children.Add(retry);
        }

        return new Border
        {
            MaxWidth = A4Width,
            Background = isError ? new SolidColorBrush(Color.FromRgb(255, 238, 238)) : SoftBackground,
            BorderBrush = isError ? Brushes.IndianRed : Rule,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 16),
            Child = content
        };
    }

    private DockPanel BuildSummary(InvoiceDocument? document)
    {
        var fields = new List<InvoiceField>
        {
            new("Numer KSeF", _invoice.KsefNumber),
            new("Numer faktury", _invoice.InvoiceNumber),
            new("Data wystawienia", _invoice.IssueDate == DateOnly.MinValue ? string.Empty : _invoice.IssueDate.ToString("dd.MM.yyyy", PolishCulture)),
            new("Sprzedawca", _invoice.SellerName),
            new("NIP sprzedawcy", _invoice.SellerNip),
            new("Nabywca", _invoice.BuyerName),
            new("Identyfikator nabywcy", _invoice.BuyerIdentifier),
            new("Kwota netto", $"{_invoice.NetAmount:N2} {_invoice.Currency}"),
            new("VAT", $"{_invoice.VatAmount:N2} {_invoice.Currency}"),
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
            foreach (var item in document.Summary)
                if (!fields.Any(x => string.Equals(x.Path, item.Key, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(new InvoiceField(item.Key, item.Value));

        var panel = new DockPanel { Margin = new Thickness(12) };
        if (document is null)
        {
            var notice = BuildInlineNotice("Pełny XML nie został jeszcze pobrany. Metadane są dostępne, a pobranie treści zostanie ponowione w następnym cyklu synchronizacji.");
            DockPanel.SetDock(notice, Dock.Top);
            panel.Children.Add(notice);
        }
        panel.Children.Add(CreateFieldGrid(fields));
        return panel;
    }

    private static DataGrid BuildFields(InvoiceDocument? document) =>
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
        FontSize = 12,
        Padding = new Thickness(8)
    };

    private static DataGrid CreateFieldGrid(IEnumerable<InvoiceField> fields)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            ItemsSource = fields,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Pole", Binding = new System.Windows.Data.Binding(nameof(InvoiceField.Path)), Width = new DataGridLength(0.42, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Wartość", Binding = new System.Windows.Data.Binding(nameof(InvoiceField.Value)), Width = new DataGridLength(0.58, DataGridLengthUnitType.Star) });
        return grid;
    }

    private string GetDocumentTitle()
    {
        var type = _invoice.InvoiceType.Trim().ToUpperInvariant();
        if (type.Contains("KOR", StringComparison.Ordinal)) return "Faktura korygująca";
        if (type.Contains("ZAL", StringComparison.Ordinal)) return "Faktura zaliczkowa";
        if (type.Contains("ROZ", StringComparison.Ordinal)) return "Faktura rozliczeniowa";
        return "Faktura VAT";
    }

    private static string FormatPaymentMethod(string value) => value.Trim() switch
    {
        "1" => "Gotówka",
        "2" => "Karta",
        "3" => "Bon",
        "4" => "Czek",
        "5" => "Kredyt",
        "6" => "Przelew",
        "7" => "Płatność mobilna",
        "" => string.Empty,
        _ => value
    };

    private static string FormatDateText(string value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd.MM.yyyy", PolishCulture)
            : value;

    private static string GetSummary(InvoiceDocument? document, string key, string fallback = "") =>
        document is not null && document.Summary.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private double EstimateFirstPageHeaderExtraHeight(InvoiceDocument? document)
    {
        static int WrappedLines(string value) => string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split(LineSeparators, StringSplitOptions.None)
                .Sum(part => Math.Max(1, (int)Math.Ceiling(part.Length / 42d)));

        var sellerLines = WrappedLines(GetSummary(document, "Sprzedawca", _invoice.SellerName))
                          + WrappedLines(GetSummary(document, "Adres sprzedawcy"));
        var buyerLines = WrappedLines(GetSummary(document, "Nabywca", _invoice.BuyerName))
                         + WrappedLines(GetSummary(document, "Adres nabywcy"));
        var additionalLines = Math.Max(0, Math.Max(sellerLines, buyerLines) - 4);
        return additionalLines * 14d;
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", PolishCulture) ?? string.Empty;
}
