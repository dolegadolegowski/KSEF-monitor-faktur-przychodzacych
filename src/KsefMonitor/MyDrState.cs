using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KsefMonitor;

internal sealed class MyDrState
{
    public const int CurrentDoctorTurnoverSchemaVersion = 1;

    public Guid ConnectionId { get; set; }
    public int DoctorTurnoverSchemaVersion { get; set; }
    public DateOnly? LastCheckLocalDate { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? ApiBlockedUntilUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public Dictionary<string, MyDrMonthSummary> Months { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<long, MyDrCachedVisit> Visits { get; set; } = new();

    public MyDrState Snapshot() => new()
    {
        ConnectionId = ConnectionId,
        DoctorTurnoverSchemaVersion = DoctorTurnoverSchemaVersion,
        LastCheckLocalDate = LastCheckLocalDate,
        LastAttemptUtc = LastAttemptUtc,
        LastSuccessfulSyncUtc = LastSuccessfulSyncUtc,
        ApiBlockedUntilUtc = ApiBlockedUntilUtc,
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
        var invalidDoctorBreakdown = false;
        foreach (var pair in Months)
        {
            if (!MyDrMonthKey.TryParse(pair.Key, out var year, out var month) || pair.Value is null) continue;
            pair.Value.Year = year;
            pair.Value.Month = month;
            if (!pair.Value.NormalizeDoctorTurnovers()) invalidDoctorBreakdown = true;
            months[pair.Key] = pair.Value;
        }
        Months = months;

        // Nie pokazujemy częściowo uszkodzonego podziału jako poprawnego. Sama
        // miesięczna suma pozostaje dostępna, a harmonogram zleci świeży odczyt.
        if (invalidDoctorBreakdown) LastCheckLocalDate = null;

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

    public bool BindToConnection(Guid connectionId)
    {
        Months ??= new Dictionary<string, MyDrMonthSummary>(StringComparer.Ordinal);
        Visits ??= new Dictionary<long, MyDrCachedVisit>();
        LastError ??= string.Empty;

        if (connectionId == Guid.Empty)
        {
            var hasBoundData = ConnectionId != Guid.Empty ||
                               LastCheckLocalDate is not null ||
                               LastAttemptUtc is not null ||
                               LastSuccessfulSyncUtc is not null ||
                               ApiBlockedUntilUtc is not null ||
                               !string.IsNullOrWhiteSpace(LastError) ||
                               Months.Count > 0 ||
                               Visits.Count > 0;
            if (!hasBoundData) return false;
            Reset(Guid.Empty);
            return true;
        }

        if (ConnectionId == connectionId) return false;
        Reset(connectionId);
        return true;
    }

    public bool UpgradeDoctorTurnoverSchema()
    {
        if (DoctorTurnoverSchemaVersion >= CurrentDoctorTurnoverSchemaVersion) return false;

        DoctorTurnoverSchemaVersion = CurrentDoctorTurnoverSchemaVersion;
        // Stary cache nie zawiera osoby realizującej wizytę. Zachowujemy ostatnie
        // sumy, ale wymuszamy jednorazowe pobranie szczegółów z MyDR.
        LastCheckLocalDate = null;
        return true;
    }

    private void Reset(Guid connectionId)
    {
        ConnectionId = connectionId;
        DoctorTurnoverSchemaVersion = CurrentDoctorTurnoverSchemaVersion;
        LastCheckLocalDate = null;
        LastAttemptUtc = null;
        LastSuccessfulSyncUtc = null;
        ApiBlockedUntilUtc = null;
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
    public bool DoctorBreakdownAvailable { get; set; }
    public List<MyDrDoctorTurnover> DoctorTurnovers { get; set; } = [];

    public MyDrMonthSummary Snapshot() => new()
    {
        Year = Year,
        Month = Month,
        GrossAmount = GrossAmount,
        VisitCount = VisitCount,
        ServiceCount = ServiceCount,
        UpdatedAtUtc = UpdatedAtUtc,
        DoctorBreakdownAvailable = DoctorBreakdownAvailable,
        DoctorTurnovers = DoctorTurnovers.Select(item => item.Snapshot()).ToList()
    };

    public bool NormalizeDoctorTurnovers()
    {
        DoctorTurnovers ??= [];
        if (!DoctorBreakdownAvailable)
        {
            DoctorTurnovers.Clear();
            return true;
        }

        var normalized = new List<MyDrDoctorTurnover>(DoctorTurnovers.Count);
        var seenIds = new HashSet<long>();
        decimal visibleTotal = 0;
        try
        {
            foreach (var item in DoctorTurnovers)
            {
                if (item is null || item.PersonnelId <= 0 || item.VisitCount <= 0 || !seenIds.Add(item.PersonnelId))
                    return InvalidateDoctorBreakdown();

                item.DisplayName = MyDrPersonnelName.NormalizeOrFallback(item.DisplayName, item.PersonnelId);
                if (item.GrossAmount == 0m) continue;
                checked { visibleTotal += item.GrossAmount; }
                normalized.Add(item);
            }
        }
        catch (OverflowException)
        {
            return InvalidateDoctorBreakdown();
        }

        if (visibleTotal != GrossAmount) return InvalidateDoctorBreakdown();
        DoctorTurnovers = MyDrDoctorTurnoverCalculator.Sort(normalized).ToList();
        return true;
    }

    private bool InvalidateDoctorBreakdown()
    {
        DoctorBreakdownAvailable = false;
        DoctorTurnovers = [];
        return false;
    }
}

internal sealed class MyDrDoctorTurnover
{
    public long PersonnelId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal GrossAmount { get; set; }
    public int VisitCount { get; set; }

    public MyDrDoctorTurnover Snapshot() => new()
    {
        PersonnelId = PersonnelId,
        DisplayName = DisplayName,
        GrossAmount = GrossAmount,
        VisitCount = VisitCount
    };
}

internal sealed record MyDrVisitTurnoverSource(
    long PersonnelId,
    string? FirstName,
    string? LastName,
    DateOnly VisitDate,
    long VisitId,
    decimal GrossAmount);

internal static class MyDrDoctorTurnoverCalculator
{
    private static readonly StringComparer PolishNameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("pl-PL"), ignoreCase: true);

    public static IReadOnlyList<MyDrDoctorTurnover> Calculate(IEnumerable<MyDrVisitTurnoverSource> visits)
    {
        ArgumentNullException.ThrowIfNull(visits);
        var aggregates = new Dictionary<long, DoctorAggregate>();

        foreach (var visit in visits)
        {
            if (visit.PersonnelId <= 0)
                throw new MyDrApiException("MyDR zwrócił wizytę bez poprawnego identyfikatora osoby realizującej.");
            if (visit.VisitId <= 0)
                throw new MyDrApiException("MyDR zwrócił wizytę bez poprawnego identyfikatora.");

            if (!aggregates.TryGetValue(visit.PersonnelId, out var aggregate))
            {
                aggregate = new DoctorAggregate(visit.PersonnelId);
                aggregates.Add(visit.PersonnelId, aggregate);
            }

            checked
            {
                aggregate.GrossAmount += visit.GrossAmount;
                aggregate.VisitCount++;
            }

            var displayName = MyDrPersonnelName.FromParts(visit.FirstName, visit.LastName);
            if (displayName is not null &&
                (aggregate.NameDate is null || visit.VisitDate > aggregate.NameDate ||
                 visit.VisitDate == aggregate.NameDate && visit.VisitId > aggregate.NameVisitId))
            {
                aggregate.DisplayName = displayName;
                aggregate.NameDate = visit.VisitDate;
                aggregate.NameVisitId = visit.VisitId;
            }
        }

        var result = aggregates.Values
            .Where(item => item.GrossAmount != 0m)
            .Select(item => new MyDrDoctorTurnover
            {
                PersonnelId = item.PersonnelId,
                DisplayName = item.DisplayName ?? MyDrPersonnelName.Fallback(item.PersonnelId),
                GrossAmount = item.GrossAmount,
                VisitCount = item.VisitCount
            });
        return Sort(result).ToList();
    }

    internal static IOrderedEnumerable<MyDrDoctorTurnover> Sort(IEnumerable<MyDrDoctorTurnover> values) =>
        values.OrderByDescending(item => item.GrossAmount)
            .ThenBy(item => item.DisplayName, PolishNameComparer)
            .ThenBy(item => item.PersonnelId);

    private sealed class DoctorAggregate(long personnelId)
    {
        public long PersonnelId { get; } = personnelId;
        public decimal GrossAmount { get; set; }
        public int VisitCount { get; set; }
        public string? DisplayName { get; set; }
        public DateOnly? NameDate { get; set; }
        public long NameVisitId { get; set; }
    }
}

internal static class MyDrDoctorTurnoverPresentation
{
    public static IReadOnlyList<MyDrDoctorTurnover> GetVisibleRows(MyDrMonthSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.DoctorBreakdownAvailable) return [];
        return MyDrDoctorTurnoverCalculator.Sort(
                summary.DoctorTurnovers.Where(item => item.GrossAmount != 0m))
            .Select(item => item.Snapshot())
            .ToList();
    }
}

internal sealed record MyDrPerformedVisitTurnover(
    MyDrVisit Visit,
    DateOnly VisitDate,
    MyDrCachedVisit CachedVisit);

internal static class MyDrMonthSummaryCalculator
{
    public static MyDrMonthSummary Calculate(
        int year,
        int month,
        DateTimeOffset updatedAtUtc,
        IEnumerable<MyDrPerformedVisitTurnover> visits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        ArgumentNullException.ThrowIfNull(visits);

        var selected = visits
            .Where(item => item.VisitDate.Year == year && item.VisitDate.Month == month)
            .ToList();
        decimal grossAmount = 0;
        var serviceCount = 0;
        checked
        {
            foreach (var item in selected)
            {
                if (item.Visit.Id <= 0 || item.CachedVisit.VisitId != item.Visit.Id ||
                    item.CachedVisit.VisitDate != item.VisitDate)
                    throw new InvalidOperationException("Cache wizyty MyDR nie odpowiada aktualnej liście wizyt.");
                grossAmount += item.CachedVisit.GrossAmount;
                serviceCount += item.CachedVisit.ServiceCount;
            }
        }

        var doctorTurnovers = MyDrDoctorTurnoverCalculator.Calculate(selected.Select(item =>
            new MyDrVisitTurnoverSource(
                item.Visit.DoctorId.GetValueOrDefault(),
                item.Visit.DoctorName,
                item.Visit.DoctorSurname,
                item.VisitDate,
                item.Visit.Id,
                item.CachedVisit.GrossAmount)));
        return new MyDrMonthSummary
        {
            Year = year,
            Month = month,
            GrossAmount = grossAmount,
            VisitCount = selected.Count,
            ServiceCount = serviceCount,
            UpdatedAtUtc = updatedAtUtc,
            DoctorBreakdownAvailable = true,
            DoctorTurnovers = doctorTurnovers.Select(item => item.Snapshot()).ToList()
        };
    }
}

internal static class MyDrPersonnelName
{
    private const int MaximumDisplayNameLength = 160;

    public static string? FromParts(string? firstName, string? lastName)
    {
        var first = Normalize(firstName);
        var last = Normalize(lastName);
        if (first.Length == 0 && last.Length == 0) return null;
        return Normalize($"{first} {last}");
    }

    public static string NormalizeOrFallback(string? value, long personnelId)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? Fallback(personnelId) : normalized;
    }

    public static string Fallback(long personnelId) =>
        FormattableString.Invariant($"Personel MyDR #{personnelId}");

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(Math.Min(value.Length, MaximumDisplayNameLength));
        var previousWasWhiteSpace = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (result.Length > 0 && !previousWasWhiteSpace) result.Append(' ');
                previousWasWhiteSpace = true;
                continue;
            }
            if (Rune.IsControl(rune)) continue;

            if (result.Length + rune.Utf16SequenceLength > MaximumDisplayNameLength) break;
            result.Append(rune.ToString());
            previousWasWhiteSpace = false;
        }

        return result.ToString().TrimEnd();
    }
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

internal static class MyDrVisitCachePolicy
{
    public static bool CanReuse(
        MyDrVisit visit,
        DateOnly visitDate,
        MyDrCachedVisit? cached,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (forceRefresh || cached is null || string.IsNullOrWhiteSpace(visit.LatestModification)) return false;

        return cached.VisitId == visit.Id &&
               cached.VisitDate == visitDate &&
               string.Equals(cached.State, visit.State?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(cached.LatestModification, visit.LatestModification, StringComparison.Ordinal);
    }
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
