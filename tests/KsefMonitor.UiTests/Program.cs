using System;
using System.Linq;
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
        Console.WriteLine("KSeFMonitor Windows layout tests: OK");
        return 0;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
