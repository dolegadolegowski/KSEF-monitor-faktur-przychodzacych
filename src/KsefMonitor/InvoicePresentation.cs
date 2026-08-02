using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsefMonitor;

internal sealed record InvoicePagePlan(
    IReadOnlyList<InvoiceLine> Lines,
    bool IsFirst,
    bool IsLast,
    int PageNumber,
    int PageCount);

internal static class InvoicePagePlanner
{
    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r"];
    // WPF pracuje w jednostkach 1/96 cala. Strona ma 794 x 1123, czyli proporcje A4.
    // Budżety uwzględniają marginesy, nagłówek dokumentu, tabelę i stopkę strony.
    private const double FirstPageLineCapacity = 700;
    private const double ContinuationPageLineCapacity = 900;
    private const double LastPageSummaryReserve = 200;
    private const double DescriptionCharactersPerLine = 38;
    private const double MaximumLineHeight = 600;

    public static IReadOnlyList<InvoicePagePlan> Plan(IReadOnlyList<InvoiceLine> lines, double firstPageHeaderExtraHeight = 0)
    {
        var firstPageCapacity = Math.Max(300, FirstPageLineCapacity - Math.Max(0, firstPageHeaderExtraHeight));
        var workingPages = new List<List<InvoiceLine>>();
        var usedHeights = new List<double>();

        foreach (var line in lines)
        {
            var lineHeight = EstimateLineHeight(line);
            if (workingPages.Count == 0)
            {
                workingPages.Add(new List<InvoiceLine>());
                usedHeights.Add(0);
            }

            var pageIndex = workingPages.Count - 1;
            var capacity = pageIndex == 0 ? firstPageCapacity : ContinuationPageLineCapacity;
            if (workingPages[pageIndex].Count > 0 && usedHeights[pageIndex] + lineHeight > capacity)
            {
                workingPages.Add(new List<InvoiceLine>());
                usedHeights.Add(0);
                pageIndex++;
            }

            workingPages[pageIndex].Add(line);
            usedHeights[pageIndex] += lineHeight;
        }

        if (workingPages.Count == 0)
        {
            workingPages.Add(new List<InvoiceLine>());
            usedHeights.Add(0);
        }

        var finalIndex = workingPages.Count - 1;
        var finalNormalCapacity = finalIndex == 0 ? firstPageCapacity : ContinuationPageLineCapacity;
        var finalCapacityWithSummary = finalNormalCapacity - LastPageSummaryReserve;
        if (usedHeights[finalIndex] > finalCapacityWithSummary)
        {
            var source = workingPages[finalIndex];
            var moved = new List<InvoiceLine>();
            var movedHeight = 0d;
            var continuationWithSummaryCapacity = ContinuationPageLineCapacity - LastPageSummaryReserve;
            var targetHeight = Math.Min(continuationWithSummaryCapacity, usedHeights[finalIndex] / 2d);

            while (source.Count > 1 && movedHeight < targetHeight)
            {
                var candidate = source[^1];
                var candidateHeight = EstimateLineHeight(candidate);
                if (movedHeight + candidateHeight > continuationWithSummaryCapacity) break;
                source.RemoveAt(source.Count - 1);
                moved.Insert(0, candidate);
                movedHeight += candidateHeight;
                usedHeights[finalIndex] -= candidateHeight;
            }

            workingPages.Add(moved);
            usedHeights.Add(movedHeight);
        }

        var pageCount = workingPages.Count;
        return workingPages
            .Select((page, index) => new InvoicePagePlan(page, index == 0, index == pageCount - 1, index + 1, pageCount))
            .ToList();
    }

    public static double EstimateLineHeight(InvoiceLine line)
    {
        var description = string.IsNullOrWhiteSpace(line.Description) ? "—" : line.Description.Trim();
        var visualLines = description
            .Split(LineSeparators, StringSplitOptions.None)
            .Sum(part => Math.Max(1, (int)Math.Ceiling(part.Length / DescriptionCharactersPerLine)));
        // Pojedynczy ekstremalnie długi opis nie może wyjść poza stronę A4.
        // Pełna treść pozostaje dostępna w zakładkach danych i surowego XML.
        return Math.Min(MaximumLineHeight, Math.Max(36, 14 + visualLines * 15));
    }
}

internal static class InvoiceValueFormatter
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

    public static string TextOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    public static string Quantity(string? value) => FormatXmlDecimal(value, "#,0.######");

    public static string Money(string? value) => FormatXmlDecimal(value, "#,0.00########");

    public static string VatRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var numeric)
            ? $"{numeric:#,0.##}%"
            : value.Trim();
    }

    private static string FormatXmlDecimal(string? value, string format)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var numeric)
            ? numeric.ToString(format, PolishCulture)
            : value.Trim();
    }
}

internal sealed record CurrencyTotal(string Currency, decimal GrossAmount);

internal static class MonthlyInvoiceSummary
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

    public static IReadOnlyList<CurrencyTotal> CalculateGrossTotals(IEnumerable<StoredInvoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        return invoices
            .GroupBy(
                invoice => NormalizeCurrency(invoice.Currency),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new CurrencyTotal(group.Key, group.Sum(invoice => invoice.GrossAmount)))
            .OrderBy(total => string.Equals(total.Currency, "PLN", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();
    }

    public static string FormatGrossTotals(IEnumerable<StoredInvoice> invoices)
    {
        var totals = CalculateGrossTotals(invoices);
        if (totals.Count == 0) return "Łącznie brutto: 0,00 PLN";

        return "Łącznie brutto: " + string.Join(
            "  •  ",
            totals.Select(total => $"{total.GrossAmount.ToString("N2", PolishCulture)} {total.Currency}"));
    }

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "PLN" : currency.Trim().ToUpperInvariant();
}
