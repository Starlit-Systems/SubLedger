using SubLedger.Core.Model;

namespace SubLedger.Core.Analytics;

/// <summary>
/// Turns real-world instants into ledger days: the user's time zone, and the time of day at which a new ledger day
/// starts. With a 06:00 cutoff, something collected at 02:00 on the 5th belongs to the 4th.
/// </summary>
public sealed class BusinessCalendar
{
    public BusinessCalendar(TimeZoneInfo zone, int cutoffMinutes)
    {
        Zone = zone;
        CutoffMinutes = Math.Clamp(cutoffMinutes, 0, 24 * 60 - 1);
    }

    public TimeZoneInfo Zone { get; }
    public int CutoffMinutes { get; }

    public DateTime ToLocal(long unix)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unix), Zone).DateTime;

    public DateOnly DateOf(long unix)
        => DateOnly.FromDateTime(ToLocal(unix).AddMinutes(-CutoffMinutes));

    public DateOnly Today(DateTimeOffset now) => DateOf(now.ToUnixTimeSeconds());
}

/// <summary>Everything the ledger knows about one day.</summary>
public sealed record DayTotal(DateOnly Date)
{
    /// <summary>Vendor value of voyages collected this day.</summary>
    public long CapturedGil { get; init; }
    public int Voyages { get; init; }

    /// <summary>Hand-kept total for this day, if one was imported or typed in.</summary>
    public long ManualGil { get; init; }
    public bool HasManual { get; init; }

    /// <summary>Corrections for this day; always counted.</summary>
    public long AdjustmentGil { get; init; }

    /// <summary>True when the day has both recorded voyages and a hand-kept total. The voyages win.</summary>
    public bool ManualExcluded => HasManual && Voyages > 0;

    /// <summary>The day's income: recorded voyages if there are any, otherwise the hand-kept total, plus corrections.</summary>
    public long IncomeGil => (Voyages > 0 ? CapturedGil : ManualGil) + AdjustmentGil;
}

public sealed record LedgerSummary
{
    public DateOnly Today { get; init; }
    public long TodayGil { get; init; }
    public int TodayVoyages { get; init; }
    public long YesterdayGil { get; init; }
    public long Last7Gil { get; init; }
    public long Last30Gil { get; init; }
    public long MonthToDateGil { get; init; }
    public long LifetimeGil { get; init; }
    public int LifetimeVoyages { get; init; }

    /// <summary>Income over the last 30 days divided by the days in that window that the ledger covers.</summary>
    public long AveragePerDay30 { get; init; }

    /// <summary>The middle value of the recorded days in the last 30.</summary>
    public long MedianDay30 { get; init; }
    public DayTotal? BestDay { get; init; }
    public DayTotal? WorstDay { get; init; }
    public DateOnly? FirstDay { get; init; }

    /// <summary>Days with at least one record. A recorded day can still total zero.</summary>
    public int RecordedDays { get; init; }

    /// <summary>Days between the first record and today with no record at all.</summary>
    public int MissingDays { get; init; }
    public long ProjectedMonthGil { get; init; }
    public long ProjectedYearGil { get; init; }
}

/// <summary>Income for one submarine or one route.</summary>
public sealed record GroupStats(string Key, string Label)
{
    public int Voyages { get; init; }
    public long TotalGil { get; init; }
    public long AveragePerVoyage => Voyages == 0 ? 0 : TotalGil / Voyages;

    /// <summary>
    /// Gil earned per 24 hours of real time, measured from one collection to the next. Zero when there are not yet
    /// two collections to measure between.
    /// </summary>
    public long GilPer24Hours { get; init; }
    public long LastCollectedUnix { get; init; }
}

public static class LedgerAnalytics
{
    /// <summary>A gap longer than this between two collections is a break in play, not a voyage length.</summary>
    private const long MaxCycleSeconds = 4 * 86400;

    public static SortedDictionary<DateOnly, DayTotal> Daily(
        IEnumerable<Voyage> voyages, IEnumerable<LedgerEntry> entries, BusinessCalendar calendar, ulong fcId = 0)
    {
        var days = new SortedDictionary<DateOnly, DayTotal>();

        foreach (var v in voyages)
        {
            if (v.Deleted || (fcId != 0 && v.FcId != fcId))
                continue;

            var date = calendar.DateOf(v.CollectedUnix);
            var day = days.GetValueOrDefault(date) ?? new DayTotal(date);
            days[date] = day with { CapturedGil = day.CapturedGil + v.TotalGil, Voyages = day.Voyages + 1 };
        }

        foreach (var e in entries)
        {
            // Entries not tied to a Free Company only show in the combined view. Spending is not income and does not
            // make a day "recorded"; it is handled by FinanceAnalytics.
            if (e.Deleted || (fcId != 0 && e.FcId != fcId) || ExpenseCategories.IsCost(e.Kind))
                continue;

            var day = days.GetValueOrDefault(e.BusinessDate) ?? new DayTotal(e.BusinessDate);
            days[e.BusinessDate] = e.Kind switch
            {
                LedgerEntryKind.HistoricalDay => day with { ManualGil = day.ManualGil + e.AmountGil, HasManual = true },
                LedgerEntryKind.Adjustment => day with { AdjustmentGil = day.AdjustmentGil + e.AmountGil },
                _ => day,
            };
        }

        return days;
    }

    public static LedgerSummary Summarize(SortedDictionary<DateOnly, DayTotal> days, DateOnly today)
    {
        if (days.Count == 0)
            return new LedgerSummary { Today = today };

        long Sum(DateOnly from, DateOnly to)
        {
            long total = 0;
            foreach (var (date, day) in days)
            {
                if (date >= from && date <= to)
                    total += day.IncomeGil;
            }

            return total;
        }

        var first = days.Keys.First();
        var from30 = today.AddDays(-29);
        var last30 = Sum(from30, today);

        // Only average over days the ledger actually spans, so a ledger that is a week old is not divided by 30.
        var coveredFrom = first > from30 ? first : from30;
        var coveredDays = Math.Max(1, today.DayNumber - coveredFrom.DayNumber + 1);
        var average = first > today ? 0 : last30 / coveredDays;

        var recent = days.Values.Where(d => d.Date >= from30 && d.Date <= today).Select(d => d.IncomeGil).Order().ToList();
        long median = 0;
        if (recent.Count > 0)
        {
            var mid = recent.Count / 2;
            median = recent.Count % 2 == 1 ? recent[mid] : (recent[mid - 1] + recent[mid]) / 2;
        }

        var spanDays = Math.Max(0, today.DayNumber - first.DayNumber + 1);
        var recordedInSpan = days.Keys.Count(d => d <= today);
        days.TryGetValue(today, out var todayTotal);

        return new LedgerSummary
        {
            Today = today,
            TodayGil = todayTotal?.IncomeGil ?? 0,
            TodayVoyages = todayTotal?.Voyages ?? 0,
            YesterdayGil = days.TryGetValue(today.AddDays(-1), out var y) ? y.IncomeGil : 0,
            Last7Gil = Sum(today.AddDays(-6), today),
            Last30Gil = last30,
            MonthToDateGil = Sum(new DateOnly(today.Year, today.Month, 1), today),
            LifetimeGil = days.Values.Sum(d => d.IncomeGil),
            LifetimeVoyages = days.Values.Sum(d => d.Voyages),
            AveragePerDay30 = average,
            MedianDay30 = median,
            BestDay = days.Values.MaxBy(d => d.IncomeGil),
            WorstDay = days.Values.MinBy(d => d.IncomeGil),
            FirstDay = first,
            RecordedDays = days.Count,
            MissingDays = Math.Max(0, spanDays - recordedInSpan),
            ProjectedMonthGil = (long)(average * 30.4375m),
            ProjectedYearGil = average * 365,
        };
    }

    public static List<GroupStats> BySubmarine(IEnumerable<Voyage> voyages, ulong fcId = 0)
    {
        var groups = new Dictionary<(ulong, uint), List<Voyage>>();
        foreach (var v in Filter(voyages, fcId))
        {
            var key = (v.FcId, v.SubRegister);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(v);
        }

        var result = new List<GroupStats>();
        foreach (var ((fc, register), list) in groups)
        {
            list.Sort((a, b) => a.CollectedUnix.CompareTo(b.CollectedUnix));
            var name = list.LastOrDefault(v => !string.IsNullOrWhiteSpace(v.SubName))?.SubName ?? $"Submarine {register}";
            long gil = 0, seconds = 0;
            for (var i = 1; i < list.Count; i++)
                Accumulate(list[i], list[i - 1], ref gil, ref seconds);

            result.Add(new GroupStats($"{fc:X}:{register}", name)
            {
                Voyages = list.Count,
                TotalGil = list.Sum(v => v.TotalGil),
                GilPer24Hours = Rate(gil, seconds),
                LastCollectedUnix = list[^1].CollectedUnix,
            });
        }

        return [.. result.OrderByDescending(g => g.TotalGil)];
    }

    public static List<GroupStats> ByRoute(IEnumerable<Voyage> voyages, ulong fcId = 0)
    {
        var bySub = new Dictionary<(ulong, uint), List<Voyage>>();
        foreach (var v in Filter(voyages, fcId))
        {
            var key = (v.FcId, v.SubRegister);
            if (!bySub.TryGetValue(key, out var list))
                bySub[key] = list = [];
            list.Add(v);
        }

        var routes = new Dictionary<string, (int Count, long Total, long Gil, long Seconds, long Last)>();
        foreach (var list in bySub.Values)
        {
            list.Sort((a, b) => a.CollectedUnix.CompareTo(b.CollectedUnix));
            for (var i = 0; i < list.Count; i++)
            {
                var v = list[i];
                var route = string.IsNullOrEmpty(v.Route) ? "?" : v.Route;
                var r = routes.GetValueOrDefault(route);
                long gil = r.Gil, seconds = r.Seconds;
                if (i > 0)
                    Accumulate(v, list[i - 1], ref gil, ref seconds);
                routes[route] = (r.Count + 1, r.Total + v.TotalGil, gil, seconds, Math.Max(r.Last, v.CollectedUnix));
            }
        }

        return [.. routes
            .Select(kv => new GroupStats(kv.Key, kv.Key)
            {
                Voyages = kv.Value.Count,
                TotalGil = kv.Value.Total,
                GilPer24Hours = Rate(kv.Value.Gil, kv.Value.Seconds),
                LastCollectedUnix = kv.Value.Last,
            })
            .OrderByDescending(g => g.TotalGil)];
    }

    private static IEnumerable<Voyage> Filter(IEnumerable<Voyage> voyages, ulong fcId)
        => voyages.Where(v => !v.Deleted && (fcId == 0 || v.FcId == fcId));

    /// <summary>
    /// Adds a voyage to a rate calculation. Its time cost is the real time since the same submarine's previous
    /// collection, which is what the submarine actually earned per day, idle time at the dock included.
    /// </summary>
    private static void Accumulate(Voyage voyage, Voyage previous, ref long gil, ref long seconds)
    {
        var cycle = voyage.CollectedUnix - previous.CollectedUnix;
        if (cycle <= 0 || cycle > MaxCycleSeconds)
            return;

        gil += voyage.TotalGil;
        seconds += cycle;
    }

    private static long Rate(long gil, long seconds)
        => seconds <= 0 ? 0 : (long)((decimal)gil * 86400m / seconds);
}
