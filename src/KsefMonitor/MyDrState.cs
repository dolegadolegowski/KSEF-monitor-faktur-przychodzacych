using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsefMonitor;

internal sealed class MyDrState
{
    public Guid ConnectionId { get; set; }
    public DateOnly? LastCheckLocalDate { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public Dictionary<string, MyDrMonthSummary> Months { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<long, MyDrCachedVisit> Visits { get; set; } = new();

    public MyDrState Snapshot() => new()
    {
        ConnectionId = ConnectionId,
        LastCheckLocalDate = LastCheckLocalDate,
        LastAttemptUtc = LastAttemptUtc,
        LastSuccessfulSyncUtc = LastSuccessfulSyncUtc,
        LastError = LastError,
        Months = Months.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(), StringComparer.Ordinal),
        Visits = Visits.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot())
    };

    public void NormalizeAfterLoad()
    {
        LastError ??= string.Empty;
        Months ??= new Dictionary<string, MyDrMonthSummary>(StringComparer.Ordinal);
        Visits ??= new Dictionary<long, MyDrCachedVisit>();

        var months = new Dictionary<string, MyDrMonthSummary>(StringComparer.Ordinal);
        foreach (var pair in Months)
        {
            if (!MyDrMonthKey.TryParse(pair.Key, out var year, out var month) || pair.Value is null) continue;
            pair.Value.Year = year;
            pair.Value.Month = month;
            months[pair.Key] = pair.Value;
        }
        Months = months;

        Visits = Visits
            .Where(pair => pair.Key > 0 && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair =>
            {
                pair.Value.VisitId = pair.Key;
                pair.Value.State ??= string.Empty;
                pair.Value.LatestModification ??= string.Empty;
                return pair.Value;
            });
    }

    public void BindToConnection(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
        {
            Reset(Guid.Empty);
            return;
        }

        if (ConnectionId == connectionId) return;
        Reset(connectionId);
    }

    private void Reset(Guid connectionId)
    {
        ConnectionId = connectionId;
        LastCheckLocalDate = null;
        LastAttemptUtc = null;
        LastSuccessfulSyncUtc = null;
        LastError = string.Empty;
        Months.Clear();
        Visits.Clear();
    }
}

internal sealed class MyDrMonthSummary
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal GrossAmount { get; set; }
    public int VisitCount { get; set; }
    public int ServiceCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public MyDrMonthSummary Snapshot() => new()
    {
        Year = Year,
        Month = Month,
        GrossAmount = GrossAmount,
        VisitCount = VisitCount,
        ServiceCount = ServiceCount,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

internal sealed class MyDrCachedVisit
{
    public long VisitId { get; set; }
    public DateOnly VisitDate { get; set; }
    public string State { get; set; } = string.Empty;
    public string LatestModification { get; set; } = string.Empty;
    public decimal GrossAmount { get; set; }
    public int ServiceCount { get; set; }

    public MyDrCachedVisit Snapshot() => new()
    {
        VisitId = VisitId,
        VisitDate = VisitDate,
        State = State,
        LatestModification = LatestModification,
        GrossAmount = GrossAmount,
        ServiceCount = ServiceCount
    };
}

internal sealed record MyDrSyncStatus(
    bool IsConfigured,
    bool IsSynchronizing,
    DateOnly? LastCheckLocalDate,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessfulSyncUtc,
    DateTimeOffset? NextScheduledSyncUtc,
    string LastError);

internal static class MyDrMonthKey
{
    public static string Create(int year, int month) => FormattableString.Invariant($"{year:0000}-{month:00}");

    public static bool TryParse(string? value, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[4] != '-') return false;
        return int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)
               && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)
               && year is >= 1 and <= 9999
               && month is >= 1 and <= 12;
    }
}

internal static class MyDrDailySchedule
{
    private static readonly TimeSpan CheckTime = TimeSpan.FromMinutes(5);

    public static TimeZoneInfo WarsawTimeZone { get; } = FindWarsawTimeZone();

    public static DateOnly GetWarsawDate(DateTimeOffset utcNow, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? WarsawTimeZone;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }

    public static DateTimeOffset GetNextCheckUtc(
        DateTimeOffset utcNow,
        DateOnly? lastCheckLocalDate,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? WarsawTimeZone;
        var today = GetWarsawDate(utcNow, zone);
        if (lastCheckLocalDate is null || lastCheckLocalDate.Value != today) return utcNow;

        var nextDate = today.AddDays(1);
        var localNext = nextDate.ToDateTime(TimeOnly.FromTimeSpan(CheckTime), DateTimeKind.Unspecified);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(localNext, zone);
        return new DateTimeOffset(nextUtc, TimeSpan.Zero);
    }

    private static TimeZoneInfo FindWarsawTimeZone()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Próba drugiego identyfikatora: IANA na Unix, Windows ID na Windows.
            }
            catch (InvalidTimeZoneException)
            {
                // Uszkodzone dane jednej strefy nie blokują próby drugiego identyfikatora.
            }
        }

        throw new TimeZoneNotFoundException("Nie znaleziono systemowej strefy czasu Europe/Warsaw.");
    }
}
