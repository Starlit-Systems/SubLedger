using System.Globalization;
using SubLedger.Core.Model;

namespace SubLedger.Core.Analytics;

/// <summary>A span of ledger days, both ends included.</summary>
public readonly record struct DateRange(DateOnly From, DateOnly To)
{
    public int Days => Math.Max(0, To.DayNumber - From.DayNumber + 1);

    public bool Contains(DateOnly date) => date >= From && date <= To;

    public override string ToString()
        => $"{From.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} to {To.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
}

public enum RangePreset
{
    Last7Days,
    Last30Days,
    Last90Days,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    ThisQuarter,
    LastQuarter,
    ThisYear,
    Lifetime,
    Custom,
}

public static class RangePresets
{
    /// <summary>The presets in the order they are offered. Custom is chosen by typing dates.</summary>
    public static readonly RangePreset[] Offered =
    [
        RangePreset.Last7Days, RangePreset.Last30Days, RangePreset.Last90Days,
        RangePreset.ThisWeek, RangePreset.LastWeek,
        RangePreset.ThisMonth, RangePreset.LastMonth,
        RangePreset.ThisQuarter, RangePreset.LastQuarter,
        RangePreset.ThisYear, RangePreset.Lifetime,
    ];

    public static string Label(RangePreset preset) => preset switch
    {
        RangePreset.Last7Days => "Last 7 days",
        RangePreset.Last30Days => "Last 30 days",
        RangePreset.Last90Days => "Last 90 days",
        RangePreset.ThisWeek => "This week",
        RangePreset.LastWeek => "Last week",
        RangePreset.ThisMonth => "This month",
        RangePreset.LastMonth => "Last month",
        RangePreset.ThisQuarter => "This quarter",
        RangePreset.LastQuarter => "Last quarter",
        RangePreset.ThisYear => "This year",
        RangePreset.Lifetime => "Lifetime",
        _ => "Custom",
    };

    /// <summary>
    /// The dates a preset covers on a given day. Weeks run Monday to Sunday. "This ..." ranges end today, since the
    /// rest of the period has not happened yet. Lifetime starts at the ledger's first record.
    /// </summary>
    public static DateRange Resolve(RangePreset preset, DateOnly today, DateOnly? firstDay)
    {
        var weekStart = StartOfWeek(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var quarterStart = StartOfQuarter(today);

        return preset switch
        {
            RangePreset.Last7Days => new DateRange(today.AddDays(-6), today),
            RangePreset.Last30Days => new DateRange(today.AddDays(-29), today),
            RangePreset.Last90Days => new DateRange(today.AddDays(-89), today),
            RangePreset.ThisWeek => new DateRange(weekStart, today),
            RangePreset.LastWeek => new DateRange(weekStart.AddDays(-7), weekStart.AddDays(-1)),
            RangePreset.ThisMonth => new DateRange(monthStart, today),
            RangePreset.LastMonth => new DateRange(monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            RangePreset.ThisQuarter => new DateRange(quarterStart, today),
            RangePreset.LastQuarter => new DateRange(quarterStart.AddMonths(-3), quarterStart.AddDays(-1)),
            RangePreset.ThisYear => new DateRange(new DateOnly(today.Year, 1, 1), today),
            _ => new DateRange(firstDay is { } first && first <= today ? first : today, today),
        };
    }

    public static DateOnly StartOfWeek(DateOnly date)
        => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly StartOfQuarter(DateOnly date)
        => new(date.Year, (date.Month - 1) / 3 * 3 + 1, 1);
}

public enum PeriodGrouping
{
    Day,
    Week,
    Month,
    Quarter,
}

/// <summary>Income for one day, week, month or quarter inside a report.</summary>
public sealed record PeriodTotal(DateOnly From, DateOnly To)
{
    /// <summary>Short axis label: "26", "23 Nov", "Nov", "Q4".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>What the label sits within: a month for days, a year for the rest.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>The period written out: "Thursday, 26 November 2026", "23 to 29 Nov 2026", "November 2026".</summary>
    public string Title { get; init; } = string.Empty;
    public long IncomeGil { get; init; }
    public int Voyages { get; init; }

    /// <summary>Days of this period that fall inside the report and the ledger's lifetime.</summary>
    public int Days { get; init; }
    public int RecordedDays { get; init; }
    public int MissingDays => Math.Max(0, Days - RecordedDays);
}

/// <summary>How much of one item came back, and what it was worth.</summary>
public sealed record ItemStats(uint ItemId, string Name, long Quantity, long TotalGil);

/// <summary>Everything a report shows for one date range.</summary>
public sealed record RangeReport
{
    public DateRange Range { get; init; }

    /// <summary>The part of the range the ledger can speak for: from its first record to today.</summary>
    public DateRange? Covered { get; init; }
    public PeriodGrouping Grouping { get; init; }
    public long TotalGil { get; init; }
    public int Voyages { get; init; }
    public int RecordedDays { get; init; }

    /// <summary>Recorded days whose income was zero.</summary>
    public int ZeroDays { get; init; }

    /// <summary>Covered days with no record at all.</summary>
    public int MissingDays { get; init; }

    /// <summary>Total divided by every covered day, recorded or not.</summary>
    public long AveragePerDay { get; init; }

    /// <summary>Total divided by recorded days only.</summary>
    public long AveragePerRecordedDay { get; init; }
    public long MedianDay { get; init; }
    public long AveragePerVoyage { get; init; }
    public DayTotal? BestDay { get; init; }
    public DayTotal? WorstDay { get; init; }

    /// <summary>Income not backed by recorded voyages: hand-kept totals and corrections.</summary>
    public long ManualGil { get; init; }

    /// <summary>Running costs (fuel, repairs) dated in the range.</summary>
    public long RunningCostsGil { get; init; }

    /// <summary>One-off purchases (parts, house, plot) dated in the range.</summary>
    public long InvestedGil { get; init; }

    /// <summary>Income after running costs.</summary>
    public long NetGil => TotalGil - RunningCostsGil;
    public IReadOnlyList<PeriodTotal> Periods { get; init; } = [];
    public IReadOnlyList<GroupStats> BySubmarine { get; init; } = [];
    public IReadOnlyList<GroupStats> ByRoute { get; init; } = [];
    public IReadOnlyList<ItemStats> ByItem { get; init; } = [];
}

public static class LedgerReports
{
    public static RangeReport Build(
        IReadOnlyList<Voyage> voyages,
        IReadOnlyList<LedgerEntry> entries,
        BusinessCalendar calendar,
        DateRange range,
        PeriodGrouping grouping,
        DateOnly today,
        ulong fcId = 0)
    {
        var allDays = LedgerAnalytics.Daily(voyages, entries, calendar, fcId);
        var days = allDays.Values.Where(d => range.Contains(d.Date)).ToList();
        var inRange = voyages.Where(v => !v.Deleted && (fcId == 0 || v.FcId == fcId) && range.Contains(calendar.DateOf(v.CollectedUnix))).ToList();

        // Days before the ledger existed, or after today, are neither recorded nor missing: they are outside what
        // the ledger can know, and are left out of averages.
        DateRange? covered = null;
        if (allDays.Count > 0)
        {
            var from = Max(range.From, allDays.Keys.First());
            var to = Min(range.To, today);
            if (from <= to)
                covered = new DateRange(from, to);
        }

        var total = days.Sum(d => d.IncomeGil);
        var coveredDays = covered?.Days ?? 0;
        var recordedInCovered = covered is { } c ? days.Count(d => c.Contains(d.Date)) : 0;
        var incomes = days.Select(d => d.IncomeGil).Order().ToList();
        var captured = days.Where(d => d.Voyages > 0).Sum(d => d.CapturedGil);
        var (runningCosts, invested) = FinanceAnalytics.InRange(entries, range, fcId);

        return new RangeReport
        {
            Range = range,
            Covered = covered,
            Grouping = grouping,
            TotalGil = total,
            Voyages = inRange.Count,
            RecordedDays = days.Count,
            ZeroDays = days.Count(d => d.IncomeGil == 0),
            MissingDays = Math.Max(0, coveredDays - recordedInCovered),
            AveragePerDay = coveredDays == 0 ? 0 : total / coveredDays,
            AveragePerRecordedDay = days.Count == 0 ? 0 : total / days.Count,
            MedianDay = Median(incomes),
            AveragePerVoyage = inRange.Count == 0 ? 0 : inRange.Sum(v => v.TotalGil) / inRange.Count,
            BestDay = days.MaxBy(d => d.IncomeGil),
            WorstDay = days.MinBy(d => d.IncomeGil),
            ManualGil = total - captured,
            RunningCostsGil = runningCosts,
            InvestedGil = invested,
            Periods = Periods(allDays, range, covered, grouping),
            BySubmarine = LedgerAnalytics.BySubmarine(inRange, fcId),
            ByRoute = LedgerAnalytics.ByRoute(inRange, fcId),
            ByItem = ByItem(inRange),
        };
    }

    /// <summary>Splits a range into days, weeks, months or quarters, including periods with nothing in them.</summary>
    public static List<PeriodTotal> Periods(
        SortedDictionary<DateOnly, DayTotal> days, DateRange range, DateRange? covered, PeriodGrouping grouping)
    {
        var result = new List<PeriodTotal>();
        var cursor = range.From;
        while (cursor <= range.To)
        {
            var start = PeriodStart(cursor, grouping);
            var end = Min(PeriodEnd(start, grouping), range.To);
            var from = Max(start, range.From);

            long income = 0;
            int voyages = 0, recorded = 0;
            for (var date = from; date <= end; date = date.AddDays(1))
            {
                if (!days.TryGetValue(date, out var day))
                    continue;
                income += day.IncomeGil;
                voyages += day.Voyages;
                if (covered is { } c && c.Contains(date))
                    recorded++;
            }

            var coveredDays = 0;
            if (covered is { } cov)
            {
                var a = Max(from, cov.From);
                var b = Min(end, cov.To);
                coveredDays = a <= b ? b.DayNumber - a.DayNumber + 1 : 0;
            }

            result.Add(Describe(new PeriodTotal(from, end)
            {
                IncomeGil = income,
                Voyages = voyages,
                Days = coveredDays,
                RecordedDays = recorded,
            }, grouping));

            cursor = end.AddDays(1);
        }

        return result;
    }

    /// <summary>
    /// For each day of a list of daily values, the average of that day and the days before it, up to
    /// <paramref name="window"/> days. Smooths out the day-to-day swing so the direction shows.
    /// </summary>
    public static long[] RollingAverage(IReadOnlyList<long> values, int window)
    {
        var result = new long[values.Count];
        long sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= window)
                sum -= values[i - window];
            result[i] = sum / Math.Min(i + 1, window);
        }

        return result;
    }

    public static List<ItemStats> ByItem(IEnumerable<Voyage> voyages)
    {
        var items = new Dictionary<uint, (string Name, long Quantity, long Gil)>();
        foreach (var v in voyages)
        {
            foreach (var line in v.Loot)
            {
                var current = items.GetValueOrDefault(line.ItemId);
                items[line.ItemId] = (line.ItemName, current.Quantity + line.Quantity, current.Gil + line.TotalGil);
            }
        }

        return [.. items
            .Select(kv => new ItemStats(kv.Key, kv.Value.Name, kv.Value.Quantity, kv.Value.Gil))
            .OrderByDescending(i => i.TotalGil).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static PeriodTotal Describe(PeriodTotal period, PeriodGrouping grouping)
    {
        var inv = CultureInfo.InvariantCulture;
        var from = period.From;
        return grouping switch
        {
            PeriodGrouping.Day => period with
            {
                Label = from.Day.ToString(inv),
                Group = from.ToString("MMM", inv),
                Title = from.ToString("dddd, d MMMM yyyy", inv),
            },
            PeriodGrouping.Week => period with
            {
                Label = from.ToString("d MMM", inv),
                Group = from.Year.ToString(inv),
                Title = $"{from.ToString("d MMM", inv)} to {period.To.ToString("d MMM yyyy", inv)}",
            },
            PeriodGrouping.Month => period with
            {
                Label = from.ToString("MMM", inv),
                Group = from.Year.ToString(inv),
                Title = from.ToString("MMMM yyyy", inv),
            },
            _ => period with
            {
                Label = $"Q{(from.Month - 1) / 3 + 1}",
                Group = from.Year.ToString(inv),
                Title = $"Q{(from.Month - 1) / 3 + 1} {from.Year.ToString(inv)}",
            },
        };
    }

    private static DateOnly PeriodStart(DateOnly date, PeriodGrouping grouping) => grouping switch
    {
        PeriodGrouping.Week => RangePresets.StartOfWeek(date),
        PeriodGrouping.Month => new DateOnly(date.Year, date.Month, 1),
        PeriodGrouping.Quarter => RangePresets.StartOfQuarter(date),
        _ => date,
    };

    private static DateOnly PeriodEnd(DateOnly start, PeriodGrouping grouping) => grouping switch
    {
        PeriodGrouping.Week => start.AddDays(6),
        PeriodGrouping.Month => start.AddMonths(1).AddDays(-1),
        PeriodGrouping.Quarter => start.AddMonths(3).AddDays(-1),
        _ => start,
    };

    private static long Median(List<long> sorted)
    {
        if (sorted.Count == 0)
            return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
