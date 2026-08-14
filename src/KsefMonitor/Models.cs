using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;

namespace KsefMonitor;

internal sealed class AppSettings
{
    public string Nip { get; set; } = string.Empty;
    public bool NotificationsEnabled { get; set; } = true;

    [JsonIgnore]
    public bool RequiresProductionToken { get; set; }

    public bool IsConfigured => NipValidator.IsValid(Nip) && !RequiresProductionToken;

    public static Uri GetBaseUri() => new("https://api.ksef.mf.gov.pl/v2/");
}

internal sealed class AppState
{
    public string ContextNip { get; set; } = string.Empty;
    public int HistoryMonthsBack { get; set; }
    public DateOnly? HistoricalBackfillBeforeIssueDate { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? LastMetadataSyncAttemptUtc { get; set; }
    public DateTimeOffset? PermanentStorageHwmDate { get; set; }
    public DateTimeOffset? ApiBlockedUntilUtc { get; set; }
    public DateTimeOffset? InvoiceDownloadBlockedUntilUtc { get; set; }
    public DateTimeOffset? LastInvoiceDownloadAttemptUtc { get; set; }
    public List<DateTimeOffset> InvoiceDownloadAttemptsUtc { get; set; } = new();
    public Dictionary<string, StoredInvoice> Invoices { get; set; } = new(StringComparer.Ordinal);

    public bool BindToContext(string normalizedNip)
    {
        if (string.IsNullOrWhiteSpace(normalizedNip)) return false;
        if (string.IsNullOrWhiteSpace(ContextNip))
        {
            // Migracja cache ze starszej wersji. Zapisany stan należał do NIP-u
            // przechowywanego w tych samych ustawieniach, więc nie usuwamy danych.
            ContextNip = normalizedNip;
            return true;
        }

        if (string.Equals(ContextNip, normalizedNip, StringComparison.Ordinal)) return false;

        ContextNip = normalizedNip;
        LastSuccessfulSyncUtc = null;
        LastMetadataSyncAttemptUtc = null;
        PermanentStorageHwmDate = null;
        InvoiceDownloadBlockedUntilUtc = null;
        HistoricalBackfillBeforeIssueDate = null;
        Invoices.Clear();
        // Globalny Retry-After oraz limity są ochroną endpointu/IP. Zachowujemy je
        // także po zmianie NIP-u, aby zapis ustawień nie omijał blokady otrzymanej
        // podczas testowania nowego kontekstu.
        return true;
    }

    public AppState Snapshot() => new()
    {
        ContextNip = ContextNip,
        HistoryMonthsBack = HistoryMonthsBack,
        HistoricalBackfillBeforeIssueDate = HistoricalBackfillBeforeIssueDate,
        LastSuccessfulSyncUtc = LastSuccessfulSyncUtc,
        LastMetadataSyncAttemptUtc = LastMetadataSyncAttemptUtc,
        PermanentStorageHwmDate = PermanentStorageHwmDate,
        ApiBlockedUntilUtc = ApiBlockedUntilUtc,
        InvoiceDownloadBlockedUntilUtc = InvoiceDownloadBlockedUntilUtc,
        LastInvoiceDownloadAttemptUtc = LastInvoiceDownloadAttemptUtc,
        InvoiceDownloadAttemptsUtc = new List<DateTimeOffset>(InvoiceDownloadAttemptsUtc),
        Invoices = Invoices.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Snapshot(),
            StringComparer.Ordinal)
    };

    public void NormalizeAfterLoad()
    {
        ContextNip ??= string.Empty;
        InvoiceDownloadAttemptsUtc ??= new List<DateTimeOffset>();
        Invoices ??= new Dictionary<string, StoredInvoice>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, StoredInvoice>(StringComparer.Ordinal);
        foreach (var pair in Invoices)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            pair.Value.NormalizeAfterLoad(pair.Key);
            normalized[pair.Key] = pair.Value;
        }
        Invoices = normalized;
    }
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
    public int XmlDownloadFailureCount { get; set; }
    public DateTimeOffset? NextXmlDownloadAttemptUtc { get; set; }
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
        Currency = string.IsNullOrWhiteSpace(metadata.Currency) ? "PLN" : metadata.Currency;
        InvoiceType = metadata.InvoiceType;
        FormCode = metadata.FormCode;
        InvoicingMode = metadata.InvoicingMode;
        HasAttachment = metadata.HasAttachment;
        IsSelfInvoicing = metadata.IsSelfInvoicing;
    }

    public StoredInvoice Snapshot() => new()
    {
        KsefNumber = KsefNumber,
        InvoiceNumber = InvoiceNumber,
        IssueDate = IssueDate,
        InvoicingDate = InvoicingDate,
        AcquisitionDate = AcquisitionDate,
        PermanentStorageDate = PermanentStorageDate,
        SellerName = SellerName,
        SellerNip = SellerNip,
        BuyerName = BuyerName,
        BuyerIdentifier = BuyerIdentifier,
        NetAmount = NetAmount,
        VatAmount = VatAmount,
        GrossAmount = GrossAmount,
        Currency = Currency,
        InvoiceType = InvoiceType,
        FormCode = FormCode,
        InvoicingMode = InvoicingMode,
        HasAttachment = HasAttachment,
        IsSelfInvoicing = IsSelfInvoicing,
        Xml = Xml,
        XmlDownloadFailureCount = XmlDownloadFailureCount,
        NextXmlDownloadAttemptUtc = NextXmlDownloadAttemptUtc,
        DiscoveredAtUtc = DiscoveredAtUtc,
        ViewedAtUtc = ViewedAtUtc,
        NotifiedAtUtc = NotifiedAtUtc
    };

    public void NormalizeAfterLoad(string fallbackKsefNumber)
    {
        KsefNumber = string.IsNullOrWhiteSpace(KsefNumber) ? fallbackKsefNumber : KsefNumber;
        InvoiceNumber ??= string.Empty;
        SellerName ??= string.Empty;
        SellerNip ??= string.Empty;
        BuyerName ??= string.Empty;
        BuyerIdentifier ??= string.Empty;
        Currency = string.IsNullOrWhiteSpace(Currency) ? "PLN" : Currency;
        InvoiceType ??= string.Empty;
        FormCode ??= string.Empty;
        InvoicingMode ??= string.Empty;
        if (XmlDownloadFailureCount < 0) XmlDownloadFailureCount = 0;
        if (!string.IsNullOrWhiteSpace(Xml))
        {
            XmlDownloadFailureCount = 0;
            NextXmlDownloadAttemptUtc = null;
        }
    }
}

internal static class KsefXmlDownloadPolicy
{
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumDocumentRetryDelay = TimeSpan.FromHours(24);

    public static bool ShouldStopQueue(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
        statusCode is { } status && (int)status >= 500;

    public static IReadOnlyList<StoredInvoice> SelectPending(
        IEnumerable<StoredInvoice> invoices,
        DateTimeOffset now,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        if (maximumCount <= 0) return Array.Empty<StoredInvoice>();

        return invoices
            .Where(invoice => string.IsNullOrWhiteSpace(invoice.Xml) &&
                              (invoice.NextXmlDownloadAttemptUtc is null ||
                               invoice.NextXmlDownloadAttemptUtc <= now))
            .OrderByDescending(invoice => invoice.IsNew)
            .ThenBy(invoice => invoice.DiscoveredAtUtc)
            .Take(maximumCount)
            .ToList();
    }

    public static TimeSpan GetRetryDelay(
        int failureCount,
        HttpStatusCode? statusCode,
        TimeSpan? serverRetryAfter = null)
    {
        var serverDelay = serverRetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? retryAfter
            : TimeSpan.Zero;

        // Błędy dokumentu (np. 400/404 zanim XML zostanie zmaterializowany)
        // dostają wykładniczy backoff: 15 min, 1 h, 4 h, 16 h, maks. 24 h.
        // Dla awarii systemowej kolejka jest zatrzymywana osobno, dlatego
        // wystarczy co najmniej standardowy interwał synchronizacji.
        var exponent = statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            ? Math.Clamp(failureCount - 1, 0, 4)
            : 0;
        var calculated = TimeSpan.FromTicks(MinimumRetryDelay.Ticks * (1L << (2 * exponent)));
        if (calculated > MaximumDocumentRetryDelay) calculated = MaximumDocumentRetryDelay;
        return serverDelay > calculated ? serverDelay : calculated;
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
    string VatRate,
    bool IsVatAmountCalculated = false,
    bool IsGrossAmountCalculated = false);

internal sealed record InvoiceField(string Path, string Value);

internal sealed class InvoiceDocument
{
    public List<InvoiceLine> Lines { get; } = new();
    public List<InvoiceField> Fields { get; } = new();
    public Dictionary<string, string> Summary { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class InvoiceRow
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");

    public required StoredInvoice Source { get; init; }
    public bool IsNew => Source.IsNew;
    public string NewLabel => Source.IsNew ? "NOWA" : string.Empty;
    public string IssueDate => Source.IssueDate == DateOnly.MinValue
        ? "—"
        : Source.IssueDate.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("pl-PL"));
    public string Seller => string.IsNullOrWhiteSpace(Source.SellerName) ? Source.SellerNip : Source.SellerName;
    public string InvoiceNumber => Source.InvoiceNumber;
    public decimal GrossAmountSortValue => Source.GrossAmount;
    public string GrossAmount => $"{Source.GrossAmount.ToString("N2", PolishCulture)} {Source.Currency}";
}

internal static class NipValidator
{
    public static string Normalize(string value) => new(value.Where(char.IsDigit).ToArray());

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Any(character => !char.IsDigit(character) && character != '-' && !char.IsWhiteSpace(character))) return false;
        var nip = Normalize(value);
        if (nip.Length != 10) return false;
        if (nip.Distinct().Count() == 1) return false;
        int[] weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        var sum = weights.Select((weight, index) => weight * (nip[index] - '0')).Sum();
        var checksum = sum % 11;
        return checksum != 10 && checksum == nip[9] - '0';
    }
}
