using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace KsefMonitor;

internal static class InvoiceXmlReader
{
    public static InvoiceDocument Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("Dokument XML jest pusty.", nameof(xml));

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 25_000_000
        };

        using var stringReader = new System.IO.StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        var xdoc = XDocument.Load(reader, LoadOptions.None);
        var result = new InvoiceDocument();
        if (xdoc.Root is null) return result;

        AddSummary(result, "Numer faktury", FirstValue(xdoc, "P_2", "NumerFaktury"));
        AddSummary(result, "Data wystawienia", FirstValue(xdoc, "P_1", "DataWystawienia"));
        AddSummary(result, "Waluta", FirstValue(xdoc, "KodWaluty"));
        AddSummary(result, "Termin płatności", FirstValue(xdoc, "TerminPlatnosci", "Termin"));
        AddSummary(result, "Forma płatności", FirstValue(xdoc, "FormaPlatnosci", "PlatnoscForma"));
        AddSummary(result, "Rachunek bankowy", FirstValue(xdoc, "NrRB", "NumerRachunkuBankowego"));

        var subject1 = FirstElement(xdoc, "Podmiot1", "Seller", "Supplier");
        var subject2 = FirstElement(xdoc, "Podmiot2", "Buyer", "Customer");
        AddSummary(result, "Sprzedawca", FirstValue(subject1, "Nazwa", "Name"));
        AddSummary(result, "NIP sprzedawcy", FirstValue(subject1, "NIP", "Nip", "TaxIdentifier"));
        AddSummary(result, "Nabywca", FirstValue(subject2, "Nazwa", "Name"));
        AddSummary(result, "NIP nabywcy", FirstValue(subject2, "NIP", "Nip", "TaxIdentifier"));

        foreach (var line in xdoc.Descendants().Where(x => NameIs(
                     x,
                     "FaWiersz",
                     "InvoiceLine",
                     "CreditNoteLine",
                     "DebitNoteLine",
                     "InvoiceRow",
                     "IncludedSupplyChainTradeLineItem")))
        {
            var quantityElement = FirstElement(line, "InvoicedQuantity", "CreditedQuantity", "BilledQuantity", "Quantity");
            var priceElement = FirstElement(line, "Price", "NetPriceProductTradePrice");
            var taxCategoryElement = FirstElement(line, "ClassifiedTaxCategory", "TaxCategory", "ApplicableTradeTax");
            var taxTotalElement = FirstElement(line, "TaxTotal", "ApplicableTradeTax");
            var allowanceElement = line.Descendants().FirstOrDefault(IsAllowance);

            var unit = FirstValue(line, "P_8A", "JednostkaMiary", "Unit");
            if (string.IsNullOrWhiteSpace(unit))
                unit = FirstAttributeValue(quantityElement, "unitCode", "unit");

            var unitNetPrice = FirstValue(line, "P_9A", "CenaJednostkowaNetto", "UnitNetPrice");
            if (string.IsNullOrWhiteSpace(unitNetPrice))
                unitNetPrice = FirstValue(priceElement, "PriceAmount", "ChargeAmount");

            var vatAmount = FirstValue(line, "P_11Vat", "VatAmount");
            if (string.IsNullOrWhiteSpace(vatAmount))
                vatAmount = FirstValue(taxTotalElement, "TaxAmount", "CalculatedAmount");

            var vatRate = FirstValue(line, "P_12", "StawkaPodatku", "VatRate");
            if (string.IsNullOrWhiteSpace(vatRate))
                vatRate = FirstValue(taxCategoryElement, "Percent", "RateApplicablePercent");

            result.Lines.Add(new InvoiceLine(
                FirstValue(line, "NrWierszaFa", "LineNumber", "ID", "LineID"),
                FirstValue(line, "P_7", "NazwaTowaruUslugi", "Description", "Name"),
                FirstValue(line, "P_8B", "Ilosc", "InvoicedQuantity", "CreditedQuantity", "BilledQuantity", "Quantity"),
                unit,
                unitNetPrice,
                FirstValue(line, "P_9B", "CenaJednostkowaBrutto", "UnitGrossPrice", "GrossPriceAmount"),
                FirstValue(line, "P_10", "Rabat", "Discount", "AllowanceAmount") is { Length: > 0 } discount
                    ? discount
                    : FirstValue(allowanceElement, "Amount", "ActualAmount"),
                FirstValue(line, "P_11", "WartoscNetto", "LineExtensionAmount", "LineTotalAmount", "NetAmount"),
                FirstValue(line, "P_11A", "WartoscBrutto", "TaxInclusiveLineExtensionAmount", "GrossLineAmount", "GrossAmount"),
                vatAmount,
                vatRate));
        }

        Flatten(xdoc.Root, $"/{xdoc.Root.Name.LocalName}", result.Fields);
        return result;
    }

    private static void Flatten(XElement element, string path, ICollection<InvoiceField> target)
    {
        foreach (var attribute in element.Attributes().Where(x => !x.IsNamespaceDeclaration))
            target.Add(new InvoiceField($"{path}/@{attribute.Name.LocalName}", attribute.Value));

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            var value = element.Value.Trim();
            if (value.Length > 0) target.Add(new InvoiceField(path, value));
            return;
        }

        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var totals = children.GroupBy(x => x.Name.LocalName).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        foreach (var child in children)
        {
            counters.TryGetValue(child.Name.LocalName, out var number);
            counters[child.Name.LocalName] = ++number;
            var suffix = totals[child.Name.LocalName] > 1 ? $"[{number}]" : string.Empty;
            Flatten(child, $"{path}/{child.Name.LocalName}{suffix}", target);
        }
    }

    private static XElement? FirstElement(XContainer document, params string[] names) =>
        document.Descendants().FirstOrDefault(x => names.Any(name => NameIs(x, name)));

    private static string FirstAttributeValue(XElement? element, params string[] names)
    {
        if (element is null) return string.Empty;
        return element.Attributes()
            .FirstOrDefault(x => names.Any(name => string.Equals(x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)))
            ?.Value.Trim() ?? string.Empty;
    }

    private static string FirstValue(XContainer? document, params string[] names)
    {
        if (document is null) return string.Empty;
        return document.Descendants().FirstOrDefault(x => names.Any(name => NameIs(x, name)))?.Value.Trim() ?? string.Empty;
    }

    private static bool NameIs(XElement element, params string[] names) =>
        names.Any(name => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowance(XElement element)
    {
        if (!NameIs(element, "AllowanceCharge", "AppliedTradeAllowanceCharge")) return false;
        var chargeIndicator = FirstValue(element, "ChargeIndicator");
        return !string.Equals(chargeIndicator, "true", StringComparison.OrdinalIgnoreCase)
               && chargeIndicator != "1";
    }

    private static void AddSummary(InvoiceDocument target, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target.Summary[label] = value;
    }
}
