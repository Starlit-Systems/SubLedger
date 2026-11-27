using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Export;
using SubLedger.Core.Model;

namespace SubLedger.Tests;

public class FinanceTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);

    private static long At(DateOnly date) => new DateTimeOffset(date.Year, date.Month, date.Day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Voyage V(DateOnly date, long gil, ulong fc = 1)
        => new() { FcId = fc, SubRegister = 1, Route = "OJ", CollectedUnix = At(date), TotalGil = gil };

    private static LedgerEntry Cost(DateOnly date, long gil, LedgerEntryKind kind = LedgerEntryKind.Investment, string category = "Submarine parts", ulong fc = 1)
        => new() { Kind = kind, BusinessDate = date, AmountGil = gil, Category = category, FcId = fc };

    private static readonly DateOnly Start = new(2026, 5, 1);

    /// <summary>Ten days of 100k a day, starting 1 May.</summary>
    private static Voyage[] TenDays() => [.. Enumerable.Range(0, 10).Select(i => V(Start.AddDays(i), 100_000))];

    [Fact]
    public void Spending_does_not_count_as_a_recorded_day()
    {
        var days = LedgerAnalytics.Daily([V(Start, 100)], [Cost(Start.AddDays(3), 500)], Utc);

        Assert.Single(days);
    }

    [Fact]
    public void Payback_date_is_the_day_net_income_reaches_the_investment()
    {
        var voyages = TenDays();
        var days = LedgerAnalytics.Daily(voyages, [], Utc);

        var summary = FinanceAnalytics.Summarize(days, [Cost(Start, 450_000)], Start.AddDays(9));

        Assert.Equal(1_000_000, summary.IncomeGil);
        Assert.Equal(450_000, summary.InvestedGil);
        Assert.Equal(550_000, summary.ProfitGil);
        Assert.Equal(Start.AddDays(4), summary.PaidBackOn);   // 500k by the fifth day
        Assert.Null(summary.ProjectedPaybackOn);
        Assert.Equal(1_000_000 / 450_000.0, summary.RecoveredShare!.Value, 6);
    }

    [Fact]
    public void Running_costs_reduce_net_income_and_delay_payback()
    {
        var days = LedgerAnalytics.Daily(TenDays(), [], Utc);
        LedgerEntry[] costs =
        [
            Cost(Start, 450_000),
            Cost(Start.AddDays(1), 100_000, LedgerEntryKind.Expense, "Repair materials"),
        ];

        var summary = FinanceAnalytics.Summarize(days, costs, Start.AddDays(9));

        Assert.Equal(900_000, summary.NetIncomeGil);
        Assert.Equal(450_000, summary.ProfitGil);
        Assert.Equal(Start.AddDays(5), summary.PaidBackOn);
        Assert.Equal(2, summary.ByCategory.Count);
        Assert.Equal("Submarine parts", summary.ByCategory[0].Category);
    }

    [Fact]
    public void A_later_purchase_moves_the_payback_date_to_when_it_was_earned_back_again()
    {
        var days = LedgerAnalytics.Daily(TenDays(), [], Utc);
        LedgerEntry[] costs = [Cost(Start, 200_000), Cost(Start.AddDays(5), 500_000)];

        var summary = FinanceAnalytics.Summarize(days, costs, Start.AddDays(9));

        // 200k is earned back on day 2. Buying 500k more on day 6 (700k invested, 600k earned) goes back into the
        // red, and day 7 (700k earned) climbs out again: that is the date that counts.
        Assert.Equal(Start.AddDays(6), summary.PaidBackOn);
    }

    [Fact]
    public void Unpaid_investment_projects_a_payback_date_from_the_recent_pace()
    {
        var days = LedgerAnalytics.Daily(TenDays(), [], Utc);

        var summary = FinanceAnalytics.Summarize(days, [Cost(Start, 1_500_000)], Start.AddDays(9));

        Assert.Null(summary.PaidBackOn);
        Assert.Equal(-500_000, summary.ProfitGil);
        Assert.Equal(100_000, summary.NetPerDay30);
        Assert.Equal(Start.AddDays(14), summary.ProjectedPaybackOn);
    }

    [Fact]
    public void Investments_before_the_first_income_start_the_timeline()
    {
        var days = LedgerAnalytics.Daily(TenDays(), [], Utc);

        var summary = FinanceAnalytics.Summarize(days, [Cost(Start.AddDays(-20), 300_000)], Start.AddDays(9));

        Assert.Equal(Start.AddDays(-20), summary.Timeline[0].Date);
        Assert.Equal(300_000, summary.Timeline[0].CumulativeInvested);
        Assert.Equal(Start.AddDays(2), summary.PaidBackOn);
    }

    [Fact]
    public void Each_free_company_has_its_own_payback()
    {
        Voyage[] voyages = [V(Start, 1_000_000, fc: 1), V(Start, 50_000, fc: 2)];
        LedgerEntry[] costs = [Cost(Start, 500_000, fc: 1), Cost(Start, 500_000, fc: 2)];

        var first = FinanceAnalytics.Summarize(LedgerAnalytics.Daily(voyages, costs, Utc, fcs: 1), costs, Start, fcs: 1);
        var second = FinanceAnalytics.Summarize(LedgerAnalytics.Daily(voyages, costs, Utc, fcs: 2), costs, Start, fcs: 2);
        var combined = FinanceAnalytics.Summarize(LedgerAnalytics.Daily(voyages, costs, Utc), costs, Start);

        Assert.Equal(500_000, first.ProfitGil);
        Assert.Equal(-450_000, second.ProfitGil);
        Assert.Equal(50_000, combined.ProfitGil);
        Assert.Equal(Start, combined.PaidBackOn);
    }

    [Fact]
    public void Removed_spending_is_ignored()
    {
        var days = LedgerAnalytics.Daily(TenDays(), [], Utc);
        var cost = Cost(Start, 450_000);
        cost.Deleted = true;

        var summary = FinanceAnalytics.Summarize(days, [cost], Start.AddDays(9));

        Assert.False(summary.HasSpending);
        Assert.Null(summary.RecoveredShare);
    }

    [Fact]
    public void Reports_show_costs_and_net_for_their_range()
    {
        LedgerEntry[] costs =
        [
            Cost(Start.AddDays(2), 30_000, LedgerEntryKind.Expense, "Repair materials"),
            Cost(Start.AddDays(20), 99_000, LedgerEntryKind.Expense, "Repair materials"),
            Cost(Start, 4_000_000),
        ];

        var report = LedgerReports.Build(TenDays(), costs, Utc, new DateRange(Start, Start.AddDays(9)), PeriodGrouping.Day, Start.AddDays(9));

        Assert.Equal(1_000_000, report.TotalGil);
        Assert.Equal(30_000, report.RunningCostsGil);
        Assert.Equal(4_000_000, report.InvestedGil);
        Assert.Equal(970_000, report.NetGil);
        Assert.Equal(10, report.RecordedDays);
    }

    [Fact]
    public void Costs_export_lists_each_purchase()
    {
        var context = new ExportContext(Utc, new Dictionary<ulong, string> { [1] = "Starlit" }, new FakeSectors(), "1");
        LedgerEntry[] costs =
        [
            new() { Kind = LedgerEntryKind.Investment, BusinessDate = Start, AmountGil = 16_000_000, Category = "Submarine parts", Note = "4 x Shark-class Hull", FcId = 1 },
            Cost(Start.AddDays(1), 30_000, LedgerEntryKind.Expense, "Repair materials"),
            new() { Kind = LedgerEntryKind.HistoricalDay, BusinessDate = Start, AmountGil = 5 },
        ];

        var lines = LedgerExport.CostsCsv(costs, context).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal("2026-05-01,investment,Submarine parts,4 x Shark-class Hull,16000000,Starlit", lines[1]);
        Assert.Equal("2026-05-02,running cost,Repair materials,,30000,Starlit", lines[2]);
    }
}

public class MultiCompanyTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);
    private static readonly DateOnly Day = new(2026, 5, 5);

    private static Voyage V(ulong fc, long gil)
        => new() { FcId = fc, SubRegister = 1, CollectedUnix = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), TotalGil = gil };

    private static LedgerEntry Hand(ulong fc, long gil)
        => new() { Kind = LedgerEntryKind.HistoricalDay, BusinessDate = Day, AmountGil = gil, FcId = fc };

    [Fact]
    public void One_companys_voyages_do_not_hide_another_companys_hand_kept_total()
    {
        var days = LedgerAnalytics.Daily([V(1, 100_000)], [Hand(1, 90_000), Hand(2, 40_000)], Utc);

        // Company 1's hand-kept total gives way to its voyages; company 2's still counts.
        Assert.Equal(140_000, days[Day].IncomeGil);
        Assert.Equal(90_000, days[Day].ExcludedManualGil);
        Assert.True(days[Day].ManualExcluded);
    }

    [Fact]
    public void A_hand_kept_total_with_no_company_gives_way_to_any_voyages()
    {
        var days = LedgerAnalytics.Daily([V(1, 100_000)], [Hand(0, 90_000)], Utc);

        Assert.Equal(100_000, days[Day].IncomeGil);
    }

    [Fact]
    public void Viewing_one_company_counts_only_its_own_records()
    {
        var days = LedgerAnalytics.Daily([V(1, 100_000)], [Hand(2, 40_000)], Utc, fcs: 2);

        Assert.Equal(40_000, days[Day].IncomeGil);
        Assert.False(days[Day].ManualExcluded);
    }
}

public class FuelTests
{
    [Fact]
    public void An_OJ_voyage_uses_nine_tanks_and_nine_hundred_credits()
    {
        using var ledger = new TempLedger();
        var voyage = ledger.Store.RecordVoyage(TempLedger.OjHaul(1_780_000_000)).Voyage!;

        Assert.Equal(9, Fuel.Tanks(voyage, new FakeSectors()));
        Assert.Equal(900, Fuel.Tanks(voyage, new FakeSectors()) * Fuel.CreditsPerTank);
    }

    [Fact]
    public void Removed_voyages_use_no_fuel()
    {
        using var ledger = new TempLedger();
        var a = ledger.Store.RecordVoyage(TempLedger.OjHaul(1_780_000_000, sub: 1)).Voyage!;
        var b = ledger.Store.RecordVoyage(TempLedger.OjHaul(1_780_000_000, sub: 2)).Voyage!;
        ledger.Store.SetVoyageDeleted(b.Id, true);

        Assert.Equal(9, Fuel.Tanks([a, b], new FakeSectors()));
    }

    [Fact]
    public void Ceruleum_tanks_are_not_offered_as_a_gil_cost()
        => Assert.DoesNotContain(ExpenseCategories.All, c => c.Name.Contains("Ceruleum", StringComparison.OrdinalIgnoreCase));
}

public class FreeCompanyFilterTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);
    private static readonly DateOnly Day = new(2026, 5, 5);

    private static Voyage V(ulong fc, long gil, uint sub = 1, long offset = 0)
        => new() { FcId = fc, SubRegister = sub, Route = "OJ", CollectedUnix = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() + offset, TotalGil = gil };

    private static readonly Voyage[] Three = [V(1, 100_000), V(2, 40_000), V(3, 7_000)];

    [Fact]
    public void Any_combination_of_free_companies_can_be_shown()
    {
        Assert.Equal(147_000, LedgerAnalytics.Daily(Three, [], Utc)[Day].IncomeGil);
        Assert.Equal(140_000, LedgerAnalytics.Daily(Three, [], Utc, FcFilter.Of([1, 2]))[Day].IncomeGil);
        Assert.Equal(47_000, LedgerAnalytics.Daily(Three, [], Utc, FcFilter.Of([3, 2]))[Day].IncomeGil);
        Assert.Equal(7_000, LedgerAnalytics.Daily(Three, [], Utc, 3UL)[Day].IncomeGil);
    }

    [Fact]
    public void Choosing_nothing_means_everything()
    {
        Assert.True(FcFilter.Of([]).IsAll);
        Assert.True(((FcFilter)0UL).IsAll);
        Assert.Equal(FcFilter.All, FcFilter.Of([0]));
    }

    [Fact]
    public void The_same_choice_in_any_order_is_the_same_filter()
    {
        Assert.Equal(FcFilter.Of([2, 1]), FcFilter.Of([1, 2, 2]));
        Assert.Equal(FcFilter.Of([2, 1]).GetHashCode(), FcFilter.Of([1, 2]).GetHashCode());
        Assert.NotEqual(FcFilter.Of([1]), FcFilter.Of([1, 2]));
        Assert.Equal(2UL, FcFilter.Of([2]).Single);
        Assert.Equal(0UL, FcFilter.Of([1, 2]).Single);
    }

    [Fact]
    public void Records_with_no_free_company_show_only_when_all_are_shown()
    {
        LedgerEntry[] entries = [new() { Kind = LedgerEntryKind.HistoricalDay, BusinessDate = Day.AddDays(1), AmountGil = 5_000, FcId = 0 }];

        Assert.True(LedgerAnalytics.Daily(Three, entries, Utc).ContainsKey(Day.AddDays(1)));
        Assert.False(LedgerAnalytics.Daily(Three, entries, Utc, FcFilter.Of([1, 2])).ContainsKey(Day.AddDays(1)));
    }

    [Fact]
    public void Free_companies_can_be_compared_side_by_side()
    {
        Voyage[] voyages = [V(1, 100_000), V(1, 100_000, offset: 86400), V(2, 40_000), V(2, 40_000, offset: 86400), V(3, 7_000)];

        var stats = LedgerAnalytics.ByFreeCompany(voyages, fc => $"FC {fc}", FcFilter.Of([1, 2]));

        Assert.Equal(["FC 1", "FC 2"], stats.Select(s => s.Label));
        Assert.Equal(200_000, stats[0].TotalGil);
        Assert.Equal(100_000, stats[0].GilPer24Hours);
        Assert.Equal(2, stats[1].Voyages);
    }

    [Fact]
    public void Payback_can_cover_several_chosen_free_companies()
    {
        LedgerEntry[] costs =
        [
            new() { Kind = LedgerEntryKind.Investment, BusinessDate = Day, AmountGil = 120_000, FcId = 1 },
            new() { Kind = LedgerEntryKind.Investment, BusinessDate = Day, AmountGil = 999_999, FcId = 3 },
        ];
        var chosen = FcFilter.Of([1, 2]);

        var summary = FinanceAnalytics.Summarize(LedgerAnalytics.Daily(Three, costs, Utc, chosen), costs, Day, chosen);

        Assert.Equal(120_000, summary.InvestedGil);
        Assert.Equal(20_000, summary.ProfitGil);
    }
}
