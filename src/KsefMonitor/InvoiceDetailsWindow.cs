using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Printing;
using Microsoft.Win32;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Błędy dokumentu i renderowania są prezentowane wewnątrz okna, aby pojedyncza faktura nie zamknęła aplikacji.")]
internal sealed class InvoiceDetailsWindow : Window, IDisposable
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
    private InvoiceDocument? _document;
    private IReadOnlyList<InvoicePagePlan> _pagePlans = Array.Empty<InvoicePagePlan>();

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
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(840, SystemParameters.WorkArea.Height - 24);
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(235, 239, 244));
        Icon = TrayIconFactory.CreateImageSource();
        Content = BuildContent();
        Loaded += OnLoaded;
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_isClosed) return;
        _isClosed = true;
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        GC.SuppressFinalize(this);
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
            previewError = $"Pobieranie zostało odłożone. Spróbuj ponownie po {exception.RetryAtUtc.ToLocalTime():dd.MM HH:mm}.";
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
        _document = document;
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

    private DockPanel BuildA4Preview(InvoiceDocument? document, string? previewError, bool canRetry)
    {
        var host = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24)
        };

        if (!string.IsNullOrWhiteSpace(previewError))
            host.Children.Add(BuildPreviewNotice(previewError, isError: true, canRetry));

        var lines = (IReadOnlyList<InvoiceLine>?)document?.Lines ?? Array.Empty<InvoiceLine>();
        var hasNetUnitPrice = lines.Any(x => !string.IsNullOrWhiteSpace(x.UnitNetPrice));
        var hasGrossUnitPrice = lines.Any(x => !string.IsNullOrWhiteSpace(x.UnitGrossPrice));
        static double MeasureBlock(FrameworkElement block)
        {
            block.Measure(new Size(A4Width - 84, double.PositiveInfinity));
            return Math.Ceiling(block.DesiredSize.Height);
        }
        var firstCapacity = Math.Max(36, A4Height - 84 - 50 - 36 - MeasureBlock(BuildDocumentHeader(document)));
        var continuationCapacity = Math.Max(36, A4Height - 84 - 50 - 36 -
            MeasureBlock(BuildContinuationHeader(new InvoicePagePlan(Array.Empty<InvoiceLine>(), false, false, 2, 2))));
        var summaryHeight = MeasureBlock(BuildTotalsAndPayment(document));
        lines = PreparePrintableLines(lines, hasNetUnitPrice, hasGrossUnitPrice, Math.Min(500, firstCapacity));
        var pages = InvoicePagePlanner.Plan(lines,
            measureHeight: line => MeasurePrintableLine(line, hasNetUnitPrice, hasGrossUnitPrice),
            firstPageLineCapacity: firstCapacity, continuationPageLineCapacity: continuationCapacity,
            summaryReserve: summaryHeight);
        _pagePlans = pages;
        foreach (var page in pages)
            host.Children.Add(BuildA4Page(document, page, hasNetUnitPrice, hasGrossUnitPrice));

        var viewer = new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(225, 230, 236)),
            CanContentScroll = false
        };
        var result = new DockPanel();
        var actions = new WrapPanel { Margin = new Thickness(8) };
        var zoom = new Slider { Minimum = 0.35, Maximum = 2, Value = 1, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var zoomLabel = new TextBlock { Text = "100%", Width = 42, VerticalAlignment = VerticalAlignment.Center };
        var fitToWidth = true;
        void ApplyZoom(double scale)
        {
            zoom.Value = Math.Clamp(scale, zoom.Minimum, zoom.Maximum);
            host.LayoutTransform = new ScaleTransform(zoom.Value, zoom.Value);
            zoomLabel.Text = $"{zoom.Value:P0}";
        }
        void Fit() => ApplyZoom(Math.Min(1, Math.Max(0.35, (viewer.ActualWidth - 55) / (A4Width + 48))));
        zoom.ValueChanged += (_, _) => ApplyZoom(zoom.Value);
        zoom.PreviewMouseLeftButtonDown += (_, _) => fitToWidth = false;
        zoom.PreviewKeyDown += (_, _) => fitToWidth = false;
        void AddAction(string text, Action action, bool enabled = true)
        {
            var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 4), IsEnabled = enabled };
            button.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception exception)
                {
                    _log.Error("Dokument", "Nie udało się zapisać lub wydrukować dokumentu.", exception);
                    System.Windows.MessageBox.Show(this, "Nie udało się zapisać lub wydrukować dokumentu. Sprawdź uprawnienia do pliku i ustawienia drukarki.", "Dokument", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            actions.Children.Add(button);
        }
        AddAction("Dopasuj", () => { fitToWidth = true; Fit(); });
        AddAction("100%", () => { fitToWidth = false; ApplyZoom(1); });
        actions.Children.Add(zoom);
        actions.Children.Add(zoomLabel);
        AddAction("Drukuj", PrintInvoice, document is not null);
        AddAction("Zapisz PDF", SavePdf, document is not null);
        AddAction("Zapisz XML", SaveXml, !string.IsNullOrWhiteSpace(_invoice.Xml));
        DockPanel.SetDock(actions, Dock.Top);
        result.Children.Add(actions);
        result.Children.Add(viewer);
        viewer.SizeChanged += (_, _) => { if (fitToWidth) Fit(); };
        return result;
    }

    private IEnumerable<Border> PrintablePages()
    {
        var lines = _document?.Lines ?? new List<InvoiceLine>();
        foreach (var plan in _pagePlans)
        {
            var page = BuildA4Page(_document, plan, lines.Any(x => !string.IsNullOrEmpty(x.UnitNetPrice)),
                lines.Any(x => !string.IsNullOrEmpty(x.UnitGrossPrice)));
            page.Effect = null;
            page.Margin = new Thickness(0);
            page.Measure(new Size(A4Width, A4Height));
            page.Arrange(new Rect(0, 0, A4Width, A4Height));
            page.UpdateLayout();
            yield return page;
        }
    }

    private void PrintInvoice()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
        var document = new FixedDocument();
        foreach (var visual in PrintablePages())
        {
            var page = new FixedPage { Width = A4Width, Height = A4Height };
            page.Children.Add(visual);
            document.Pages.Add(new PageContent { Child = page });
        }
        dialog.PrintDocument(document.DocumentPaginator, $"Faktura {_invoice.InvoiceNumber}");
    }

    private SaveFileDialog SaveDialog(string extension, string filter) => new()
    {
        DefaultExt = extension, Filter = filter, AddExtension = true,
        FileName = string.Concat(_invoice.InvoiceNumber.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + extension
    };

    private void SaveXml()
    {
        var dialog = SaveDialog(".xml", "Oryginalny XML faktury|*.xml");
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, _invoice.Xml, new System.Text.UTF8Encoding(false));
    }

    private void SavePdf()
    {
        var dialog = SaveDialog(".pdf", "Dokument PDF|*.pdf");
        if (dialog.ShowDialog(this) != true) return;
        var images = CreatePdfImages();
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var destination = File.Create(temporary)) InvoicePdfWriter.Write(destination, images);
            File.Move(temporary, dialog.FileName, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal List<InvoicePageImage> CreatePdfImages()
    {
        var images = new List<InvoicePageImage>();
        foreach (var page in PrintablePages())
        {
            var bitmap = new RenderTargetBitmap((int)(A4Width * 1.5), (int)(A4Height * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            images.Add(new InvoicePageImage(memory.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight));
        }
        return images;
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
            panel.Children.Add(BuildLineGrid(line, isHeader: false, MeasurePrintableLine(line, hasNetUnitPrice, hasGrossUnitPrice), hasNetUnitPrice, hasGrossUnitPrice));
        return panel;
    }

    private static Grid BuildLineGrid(
        InvoiceLine? line,
        bool isHeader,
        double height,
        bool hasNetUnitPrice,
        bool hasGrossUnitPrice)
    {
        var grid = new Grid { Height = height, MinHeight = 36, Background = isHeader ? Accent : Brushes.White };
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
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
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
                Child = text
            };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return grid;
    }

    internal static double MeasurePrintableLine(InvoiceLine line, bool net = true, bool gross = false)
    {
        var row = BuildLineGrid(line, false, double.NaN, net, gross);
        row.Measure(new Size(A4Width - 84, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(36, row.DesiredSize.Height));
    }

    internal static IReadOnlyList<InvoiceLine> PreparePrintableLines(IReadOnlyList<InvoiceLine> lines, bool net, bool gross,
        double maximumHeight = 500)
    {
        var result = new List<InvoiceLine>();
        foreach (var original in lines)
        {
            var remaining = original.Description;
            var line = original;
            while (MeasurePrintableLine(line with { Description = remaining }, net, gross) > maximumHeight && remaining.Length > 1)
            {
                var low = 1;
                var high = remaining.Length;
                while (low < high)
                {
                    var middle = (low + high + 1) / 2;
                    if (MeasurePrintableLine(line with { Description = remaining[..middle] }, net, gross) <= maximumHeight) low = middle;
                    else high = middle - 1;
                }
                // Nie rozdzielamy pary UTF-16 w połowie znaku.
                if (low > 1 && char.IsHighSurrogate(remaining[low - 1]) && char.IsLowSurrogate(remaining[low])) low--;
                result.Add(line with { Description = remaining[..low] });
                remaining = remaining[low..];
                line = new InvoiceLine($"{original.Number} cd.", remaining, "", "", "", "", "", "", "", "", "");
            }
            result.Add(line with { Description = remaining });
        }
        return result;
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
        AddPaymentRow(payment, "Status", GetSummary(document, "Status płatności"));
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
                string.IsNullOrWhiteSpace(payable) ? "WARTOŚĆ DOKUMENTU" : "DO ZAPŁATY WG DOKUMENTU",
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
            var notice = BuildInlineNotice("Pełny XML nie jest jeszcze dostępny. Metadane są zapisane lokalnie. W zakładce Dokument A4 użyj „Spróbuj ponownie” po wskazanej przerwie.");
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
