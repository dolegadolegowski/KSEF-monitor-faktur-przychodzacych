using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KsefMonitor;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var original = new InvoiceLine("1", new string('W', 12000) + " koniec", "1", "szt.",
            "100.00", "", "10.00", "100.00", "123.00", "23.00", "23");
        var lines = InvoiceDetailsWindow.PreparePrintableLines([original], true, false);
        Require(lines.Count > 1, "Długi opis nie został podzielony.");
        Require(string.Concat(lines.Select(x => x.Description)) == original.Description, "Podział zgubił opis.");
        Require(lines.Count(x => !string.IsNullOrEmpty(x.GrossAmount)) == 1, "Kontynuacja powiela kwoty.");
        Require(lines.All(x => InvoiceDetailsWindow.MeasurePrintableLine(x) <= 500), "Opis wychodzi poza stronę.");
        var pages = InvoicePagePlanner.Plan(lines, measureHeight: x => InvoiceDetailsWindow.MeasurePrintableLine(x));
        Require(pages.SelectMany(x => x.Lines).Count() == lines.Count, "Paginacja zgubiła fragmenty.");
        var withoutDiscount = original with { Description = "opis", Discount = "" };
        Require(InvoiceDetailsWindow.MeasurePrintableLine(withoutDiscount with { Discount = "10.00" }) >=
            InvoiceDetailsWindow.MeasurePrintableLine(withoutDiscount), "Pomiar nie uwzględnia rabatu.");
        TestInvoiceWindow();
        Console.WriteLine("KSeFMonitor Windows layout tests: OK");
        return 0;
    }

    private static void TestInvoiceWindow()
    {
        var invoice = new StoredInvoice
        {
            InvoiceNumber = "TEST/1", KsefNumber = "UI-TEST", IssueDate = new DateOnly(2026, 10, 1),
            NetAmount = 100, VatAmount = 23, GrossAmount = 123,
            Xml = "<Faktura><Fa><P_15>123</P_15><FaWiersz><P_7>Pozycja testowa</P_7><P_11>100</P_11><P_12>23</P_12></FaWiersz></Fa></Faktura>"
        };
        using var window = new InvoiceDetailsWindow(invoice,
            new ApplicationLog(Path.Combine(Path.GetTempPath(), "ksef-ui-tests.log")),
            (_, _) => throw new InvalidOperationException("Test UI nie może odpytuje API."));
        var frame = new DispatcherFrame();
        var displayed = false;
        window.InvoiceContentDisplayed += (_, _) => { displayed = true; frame.Continue = false; };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => frame.Continue = false;
        window.Width = 680;
        window.Height = 460;
        timeout.Start();
        window.Show();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        Require(displayed, "Okno nie wyświetliło zapisanej faktury bez połączenia API.");
        window.UpdateLayout();
        var idle = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => idle.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(idle);
        var viewer = Descendants<ScrollViewer>(window).First(view => view.Content is StackPanel panel &&
            panel.Children.OfType<Border>().Any(border => border.Width == 794));
        Require(viewer.ViewportWidth > 0 && viewer.ExtentWidth <= viewer.ViewportWidth + 2,
            "Dopasowanie A4 wymaga poziomego przewijania w małym oknie.");
        window.Width = 620;
        window.Height = 340;
        window.UpdateLayout();
        var compact = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => compact.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(compact);
        Require(viewer.ViewportHeight > 0 && viewer.ExtentWidth <= viewer.ViewportWidth + 2,
            "Dokument nie mieści się w przestrzeni roboczej odpowiadającej większemu skalowaniu Windows.");
        var images = window.CreatePdfImages();
        Require(images.Count > 0 && images[0].Jpeg.Length > 1000, "Nie wyrenderowano stron eksportu PDF.");
        using var pdf = new MemoryStream();
        InvoicePdfWriter.Write(pdf, images);
        Require(Encoding.Latin1.GetString(pdf.ToArray()).StartsWith("%PDF-1.4", StringComparison.Ordinal), "Nie wygenerowano PDF rzeczywistej faktury.");
        window.Close();
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
