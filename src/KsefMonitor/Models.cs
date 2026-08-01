using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsefMonitor;

internal enum KsefEnvironment
{
    Test,
    Demo,
    Production
}

internal sealed class AppSettings
{
    public KsefEnvironment Environment { get; set; } = KsefEnvironment.Test;
    public string Nip { get; set; } = string.Empty;
    public bool NotificationsEnabled { get; set; } = true;

    public bool IsConfigured => NipValidator.IsValid(Nip);

    public Uri GetBaseUri() => Environment switch
    {
        KsefEnvironment.Test => new Uri("https://api-test.ksef.mf.gov.pl/v2/"),
        KsefEnvironment.Demo => new Uri("https://api-demo.ksef.mf.gov.pl/v2/"),
        _ => new Uri("https://api.ksef.mf.gov.pl/v2/")
    };

    public string EnvironmentLabel => Environment switch
    {
        KsefEnvironment.Test => "TEST",
        KsefEnvironment.Demo => "DEMO",
        _ => "PRODUKCJA"
    };
}

internal sealed class AppState
{
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? LastMetadataSyncAttemptUtc { get; set; }
    public DateTimeOffset? PermanentStorageHwmDate { get; set; }
    public List<DateTimeOffset> InvoiceDownloadAttemptsUtc { get; set; } = new();
    public Dictionary<string, StoredInvoice> Invoices { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class StoredInvoice
{
    public string KsefNumber { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateOnly IssueDate { get; set; }
    public DateTimeOffset? InvoicingDate { get; set; }
    public DateTimeOffset? AcquisitionDate { get; set; }
    public DateTimeOffset? PermanentStorageDate { get; set; }
    public string SellerName { get; set; } = string.Empty;
    public string SellerNip { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerIdentifier { get; set; } = string.Empty;
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public string Currency { get; set; } = "PLN";
    public string InvoiceType { get; set; } = string.Empty;
    public string FormCode { get; set; } = string.Empty;
    public string InvoicingMode { get; set; } = string.Empty;
    public bool HasAttachment { get; set; }
    public bool IsSelfInvoicing { get; set; }
    public string? Xml { get; set; }
    public DateTimeOffset DiscoveredAtUtc { get; set; }
    public DateTimeOffset? ViewedAtUtc { get; set; }
    public DateTimeOffset? NotifiedAtUtc { get; set; }

    public bool IsNew => ViewedAtUtc is null;

    public void UpdateFrom(InvoiceMetadata metadata)
    {
        InvoiceNumber = metadata.InvoiceNumber;
        IssueDate = metadata.IssueDate;
        InvoicingDate = metadata.InvoicingDate;
        AcquisitionDate = metadata.AcquisitionDate;
        PermanentStorageDate = metadata.PermanentStorageDate;
        SellerName = metadata.SellerName;
        SellerNip = metadata.SellerNip;
        BuyerName = metadata.BuyerName;
        BuyerIdentifier = metadata.BuyerIdentifier;
        NetAmount = metadata.NetAmount;
        VatAmount = metadata.VatAmount;
        GrossAmount = metadata.GrossAmount;
        Currency = metadata.Currency;
        InvoiceType = metadata.InvoiceType;
        FormCode = metadata.FormCode;
        InvoicingMode = metadata.InvoicingMode;
        HasAttachment = metadata.HasAttachment;
        IsSelfInvoicing = metadata.IsSelfInvoicing;
    }
}

internal sealed record InvoiceMetadata(
    string KsefNumber,
    string InvoiceNumber,
    DateOnly IssueDate,
    DateTimeOffset? InvoicingDate,
    DateTimeOffset? AcquisitionDate,
    DateTimeOffset? PermanentStorageDate,
    string SellerName,
    string SellerNip,
    string BuyerName,
    string BuyerIdentifier,
    decimal NetAmount,
    decimal VatAmount,
    decimal GrossAmount,
    string Currency,
    string InvoiceType,
    string FormCode,
    string InvoicingMode,
    bool HasAttachment,
    bool IsSelfInvoicing);

internal sealed record MetadataQueryResult(
    IReadOnlyList<InvoiceMetadata> Invoices,
    DateTimeOffset? PermanentStorageHwmDate);

internal sealed record InvoiceLine(
    string Number,
    string Description,
    string Quantity,
    string Unit,
    string UnitNetPrice,
    string UnitGrossPrice,
    string Discount,
    string NetAmount,
    string GrossAmount,
    string VatAmount,
    string VatRate);

internal sealed record InvoiceField(string Path, string Value);

internal sealed class InvoiceDocument
{
    public List<InvoiceLine> Lines { get; } = new();
    public List<InvoiceField> Fields { get; } = new();
    public Dictionary<string, string> Summary { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class InvoiceRow
{
    public required StoredInvoice Source { get; init; }
    public string NewLabel => Source.IsNew ? "NOWA" : string.Empty;
    public string IssueDate => Source.IssueDate.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("pl-PL"));
    public string Seller => string.IsNullOrWhiteSpace(Source.SellerName) ? Source.SellerNip : Source.SellerName;
    public string InvoiceNumber => Source.InvoiceNumber;
    public string GrossAmount => $"{Source.GrossAmount:N2} {Source.Currency}";
}

internal static class NipValidator
{
    public static string Normalize(string value) => new(value.Where(char.IsDigit).ToArray());

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var nip = Normalize(value);
        if (nip.Length != 10) return false;
        int[] weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        var sum = weights.Select((weight, index) => weight * (nip[index] - '0')).Sum();
        var checksum = sum % 11;
        return checksum != 10 && checksum == nip[9] - '0';
    }
}
