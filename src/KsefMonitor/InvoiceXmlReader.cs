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

        AddSummary(result, "Numer faktury", FirstValue(xdoc, "P_2", "NumerFaktury", "ID"));
        AddSummary(result, "Data wystawienia", FirstValue(xdoc, "P_1", "DataWystawienia", "IssueDate"));
        AddSummary(result, "Data sprzedaży", FirstValue(xdoc, "P_6", "DataSprzedazy", "ActualDeliveryDate"));
        AddSummary(result, "Waluta", FirstValue(xdoc, "KodWaluty", "DocumentCurrencyCode"));
        AddSummary(result, "Termin płatności", FirstValue(xdoc, "Termin", "PaymentDueDate", "DueDate"));
        AddSummary(result, "Forma płatności", FirstValue(xdoc, "FormaPlatnosci", "PlatnoscForma", "PaymentMeansCode"));
        AddSummary(result, "Do zapłaty", FirstValue(xdoc, "DoZaplaty", "PayableAmount", "DuePayableAmount"));

        var subject1 = FirstElement(xdoc, "Podmiot1", "AccountingSupplierParty", "SellerTradeParty", "Seller", "Supplier");
        var subject2 = FirstElement(xdoc, "Podmiot2", "AccountingCustomerParty", "BuyerTradeParty", "Buyer", "Customer");
        AddSummary(result, "Sprzedawca", FirstValue(subject1, "Nazwa", "Name"));
        AddSummary(result, "NIP sprzedawcy", FirstValue(subject1, "NIP", "Nip", "TaxIdentifier", "CompanyID"));
        AddSummary(result, "Adres sprzedawcy", FormatAddress(subject1));
        AddSummary(result, "Nabywca", FirstValue(subject2, "Nazwa", "Name"));
        AddSummary(result, "NIP nabywcy", FirstValue(subject2, "NIP", "Nip", "TaxIdentifier", "CompanyID"));
        AddSummary(result, "Adres nabywcy", FormatAddress(subject2));

        var paymentAccount = FirstElement(xdoc, "RachunekBankowy", "PayeeFinancialAccount", "SpecifiedTradeSettlementPaymentMeans");
        AddSummary(result, "Rachunek bankowy", FirstValue(paymentAccount, "NrRB", "IBANID", "ID"));

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

    private static XElement? FirstElement(XContainer document, params string[] names)
    {
        foreach (var name in names)
        {
            var result = document.Descendants().FirstOrDefault(x => NameIs(x, name));
            if (result is not null) return result;
        }
        return null;
    }

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
        foreach (var name in names)
        {
            var result = document.Descendants().FirstOrDefault(x => NameIs(x, name));
            if (result is not null) return result.Value.Trim();
        }
        return string.Empty;
    }

    private static string FormatAddress(XContainer? party)
    {
        if (party is null) return string.Empty;
        var line1 = FirstValue(party, "AdresL1");
        var line2 = FirstValue(party, "AdresL2");
        if (!string.IsNullOrWhiteSpace(line1) || !string.IsNullOrWhiteSpace(line2))
            return string.Join(Environment.NewLine, new[] { line1, line2 }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var street = FirstValue(party, "StreetName", "LineOne");
        var building = FirstValue(party, "BuildingNumber");
        var postalCode = FirstValue(party, "PostalZone", "PostcodeCode");
        var city = FirstValue(party, "CityName");
        var country = FirstValue(party, "IdentificationCode", "CountryID");
        var streetLine = string.Join(" ", new[] { street, building }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var cityLine = string.Join(" ", new[] { postalCode, city }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.Join(Environment.NewLine, new[] { streetLine, cityLine, country }.Where(x => !string.IsNullOrWhiteSpace(x)));
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
