using SubLedger.Core.Model;

namespace SubLedger.Core.Analytics;

/// <summary>Where the money went and whether the fleet has earned it back, up to a given day.</summary>
public sealed record FinanceSummary
{
    /// <summary>One-off spending the fleet has to earn back: parts, house, plot.</summary>
    public long InvestedGil { get; init; }

    /// <summary>Ongoing spending: fuel, repair materials.</summary>
    public long RunningCostsGil { get; init; }

    /// <summary>Everything the submarines earned (recorded voyages, hand-kept totals, corrections).</summary>
    public long IncomeGil { get; init; }

    /// <summary>Income after running costs.</summary>
    public long NetIncomeGil => IncomeGil - RunningCostsGil;

    /// <summary>What is left once the investment is paid for. Negative while it is still being earned back.</summary>
    public long ProfitGil => NetIncomeGil - InvestedGil;

    /// <summary>Net income as a share of the investment: 1.0 means exactly paid back. Null with no investment.</summary>
    public double? RecoveredShare => InvestedGil > 0 ? (double)NetIncomeGil / InvestedGil : null;

    /// <summary>The ledger day on which net income last caught up with the money invested, if it has.</summary>
    public DateOnly? PaidBackOn { get; init; }

    /// <summary>When the investment will be paid back at the last 30 days' pace, if it has not been already.</summary>
    public DateOnly? ProjectedPaybackOn { get; init; }

    /// <summary>Income after running costs per day, averaged over the last 30 days.</summary>
    public long NetPerDay30 { get; init; }

    /// <summary>Day by day, from the first income or spending to today.</summary>
    public IReadOnlyList<FinancePoint> Timeline { get; init; } = [];
    public IReadOnlyList<CategoryTotal> ByCategory { get; init; } = [];
    public bool HasSpending => InvestedGil != 0 || RunningCostsGil != 0;
}

/// <summary>Running totals at the end of one day.</summary>
public readonly record struct FinancePoint(DateOnly Date, long CumulativeNetIncome, long CumulativeInvested);

public sealed record CategoryTotal(string Category, LedgerEntryKind Kind, long TotalGil, int Entries);

public static class FinanceAnalytics
{
    public static FinanceSummary Summarize(
        SortedDictionary<DateOnly, DayTotal> days, IEnumerable<LedgerEntry> entries, DateOnly today, FcFilter fcs = default)
    {
        var costs = entries
            .Where(e => !e.Deleted && ExpenseCategories.IsCost(e.Kind) && fcs.Includes(e.FcId))
            .ToList();

        var invested = costs.Where(e => e.Kind == LedgerEntryKind.Investment).Sum(e => e.AmountGil);
        var running = costs.Where(e => e.Kind == LedgerEntryKind.Expense).Sum(e => e.AmountGil);
        var income = days.Values.Where(d => d.Date <= today).Sum(d => d.IncomeGil);

        var byCategory = costs
            .GroupBy(e => (Category: string.IsNullOrWhiteSpace(e.Category) ? "Uncategorised" : e.Category, e.Kind))
            .Select(g => new CategoryTotal(g.Key.Category, g.Key.Kind, g.Sum(e => e.AmountGil), g.Count()))
            .OrderByDescending(c => c.TotalGil)
            .ToList();

        // Day-by-day running totals, from whichever came first, money in or money out.
        var investedByDay = new Dictionary<DateOnly, long>();
        var runningByDay = new Dictionary<DateOnly, long>();
        foreach (var e in costs)
        {
            var target = e.Kind == LedgerEntryKind.Investment ? investedByDay : runningByDay;
            target[e.BusinessDate] = target.GetValueOrDefault(e.BusinessDate) + e.AmountGil;
        }

        var starts = new List<DateOnly>();
        if (days.Count > 0)
            starts.Add(days.Keys.First());
        if (costs.Count > 0)
            starts.Add(costs.Min(e => e.BusinessDate));

        var timeline = new List<FinancePoint>();
        DateOnly? paidBackOn = null;
        if (starts.Count > 0)
        {
            var start = starts.Min();
            long net = 0, spent = 0;
            var above = false;
            for (var date = start; date <= today; date = date.AddDays(1))
            {
                if (days.TryGetValue(date, out var day))
                    net += day.IncomeGil;
                net -= runningByDay.GetValueOrDefault(date);
                spent += investedByDay.GetValueOrDefault(date);
                timeline.Add(new FinancePoint(date, net, spent));

                // Later purchases can push the fleet back into the red, so the date that counts is the most
                // recent time it climbed back out.
                var nowAbove = spent > 0 && net >= spent;
                if (nowAbove && !above)
                    paidBackOn = date;
                above = nowAbove;
            }

            if (!above)
                paidBackOn = null;
        }

        // The pace of the last 30 days, over the days of those 30 that the ledger covers.
        var from30 = today.AddDays(-29);
        var first = timeline.Count > 0 ? timeline[0].Date : today;
        var windowStart = first > from30 ? first : from30;
        var windowDays = Math.Max(1, today.DayNumber - windowStart.DayNumber + 1);
        var income30 = days.Values.Where(d => d.Date >= from30 && d.Date <= today).Sum(d => d.IncomeGil);
        var running30 = costs.Where(e => e.Kind == LedgerEntryKind.Expense && e.BusinessDate >= from30 && e.BusinessDate <= today).Sum(e => e.AmountGil);
        var netPerDay = (income30 - running30) / windowDays;

        DateOnly? projected = null;
        var remaining = invested - (income - running);
        if (invested > 0 && paidBackOn is null && remaining > 0 && netPerDay > 0)
        {
            var daysToGo = (remaining + netPerDay - 1) / netPerDay;
            if (daysToGo <= 3650)
                projected = today.AddDays((int)daysToGo);
        }

        return new FinanceSummary
        {
            InvestedGil = invested,
            RunningCostsGil = running,
            IncomeGil = income,
            PaidBackOn = paidBackOn,
            ProjectedPaybackOn = projected,
            NetPerDay30 = netPerDay,
            Timeline = timeline,
            ByCategory = byCategory,
        };
    }

    /// <summary>Spending dated inside a range.</summary>
    public static (long RunningCosts, long Invested) InRange(IEnumerable<LedgerEntry> entries, DateRange range, FcFilter fcs = default)
    {
        long running = 0, invested = 0;
        foreach (var e in entries)
        {
            if (e.Deleted || !fcs.Includes(e.FcId) || !range.Contains(e.BusinessDate))
                continue;
            if (e.Kind == LedgerEntryKind.Expense)
                running += e.AmountGil;
            else if (e.Kind == LedgerEntryKind.Investment)
                invested += e.AmountGil;
        }

        return (running, invested);
    }
}
