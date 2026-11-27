using SubLedger.Core.Analytics;
using SubLedger.Core.Model;

namespace SubLedger.Tests;

public class FarmingTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);
    private static readonly DateOnly Today = new(2026, 5, 20);
    private static readonly string[] Routes = ["OJ", "JORZ"];

    private static long At(int day) => new DateTimeOffset(2026, 5, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Voyage V(int day, long gil, uint sub, string route, ulong fc = 1)
        => new() { FcId = fc, SubRegister = sub, SubName = $"Sub {sub}", Route = route, CollectedUnix = At(day), TotalGil = gil };

    // Sub 1 starts farming OJ on the 5th; sub 2 levels until it moves to JORZ on the 9th.
    private static readonly Voyage[] Fleet =
    [
        V(1, 1_000, 1, "MA"), V(2, 1_000, 2, "MA"),
        V(5, 100_000, 1, "OJ"), V(6, 2_000, 2, "MAB"),
        V(9, 100_000, 1, "OJ"), V(9, 150_000, 2, "JORZ"),
        V(10, 100_000, 1, "OJ"), V(11, 150_000, 2, "JORZ"),
    ];

    [Fact]
    public void Route_lists_are_read_as_route_letters()
        => Assert.Equal(["OJ", "JORZ", "MROJZ"], FarmingPeriod.ParseRoutes(" oj, JORZ;MROJZ  oj "));

    [Fact]
    public void Farming_can_start_with_the_first_submarine_or_the_whole_fleet()
    {
        var first = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.FirstSubmarine);
        var all = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.WholeFleet);

        Assert.Equal(new DateOnly(2026, 5, 5), first.Starts[1]);
        Assert.Equal(new DateOnly(2026, 5, 9), all.Starts[1]);
    }

    [Fact]
    public void A_submarine_that_never_farmed_is_not_waited_for()
    {
        Voyage[] voyages = [.. Fleet, V(12, 500, 3, "MA")];

        var all = FarmingPeriod.Detect(voyages, Utc, Routes, FarmingStart.WholeFleet);

        Assert.Equal(new DateOnly(2026, 5, 9), all.Starts[1]);
    }

    [Fact]
    public void Each_free_company_has_its_own_start_and_one_that_never_farmed_has_none()
    {
        Voyage[] voyages = [.. Fleet, V(15, 90_000, 1, "OJ", fc: 2), V(3, 900, 1, "MA", fc: 3)];

        var first = FarmingPeriod.Detect(voyages, Utc, Routes, FarmingStart.FirstSubmarine);

        Assert.Equal(new DateOnly(2026, 5, 15), first.Starts[2]);
        Assert.False(first.Starts.ContainsKey(3));
        Assert.Equal(new DateOnly(2026, 5, 5), first.FirstFor(FcFilter.All));
        Assert.Equal(new DateOnly(2026, 5, 15), first.FirstFor(2));
        Assert.Null(first.FirstFor(3));
        Assert.False(first.Covers(3, new DateOnly(2026, 5, 18)));
    }

    [Fact]
    public void A_farming_report_leaves_out_the_levelling_days()
    {
        var farming = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.FirstSubmarine);
        var range = RangePresets.Resolve(RangePreset.FarmingFirstSub, Today, farming.FirstFor(FcFilter.All));

        var lifetime = LedgerReports.Build(Fleet, [], Utc, new DateRange(new DateOnly(2026, 5, 1), Today), PeriodGrouping.Day, Today);
        var report = LedgerReports.Build(Fleet, [], Utc, range, PeriodGrouping.Day, Today, farming: farming);

        Assert.Equal(new DateOnly(2026, 5, 5), range.From);
        // Sub 1 from the 5th, sub 2 only from its own first JORZ voyage on the 9th: its levelling on the 6th is out.
        Assert.Equal(lifetime.TotalGil - 4_000, report.TotalGil);
        Assert.Equal(600_000, report.TotalGil);
        Assert.Equal(5, report.Voyages);
        Assert.Equal(16, report.Covered!.Value.Days);
        Assert.Equal(report.TotalGil / 16, report.AveragePerDay);
        Assert.True(report.AveragePerDay > lifetime.AveragePerDay);
    }

    [Fact]
    public void Counting_from_the_first_submarine_leaves_out_the_others_until_they_farm_too()
    {
        var farming = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.FirstSubmarine);

        Assert.True(farming.Covers(Fleet[2], new DateOnly(2026, 5, 5)));    // sub 1 on OJ
        Assert.False(farming.Covers(Fleet[3], new DateOnly(2026, 5, 6)));   // sub 2 still levelling
        Assert.True(farming.Covers(Fleet[5], new DateOnly(2026, 5, 9)));    // sub 2's first JORZ
    }

    [Fact]
    public void Whole_fleet_farming_also_leaves_out_the_days_one_submarine_was_still_levelling()
    {
        var farming = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.WholeFleet);
        var range = RangePresets.Resolve(RangePreset.FarmingAllSubs, Today, farming.FirstFor(FcFilter.All));

        var report = LedgerReports.Build(Fleet, [], Utc, range, PeriodGrouping.Day, Today, farming: farming);

        Assert.Equal(new DateOnly(2026, 5, 9), range.From);
        Assert.Equal(500_000, report.TotalGil);
        Assert.Equal(4, report.Voyages);
    }

    [Fact]
    public void Hand_kept_totals_from_before_farming_are_left_out_too()
    {
        var farming = FarmingPeriod.Detect(Fleet, Utc, Routes, FarmingStart.FirstSubmarine);
        LedgerEntry[] entries =
        [
            new() { Kind = LedgerEntryKind.HistoricalDay, FcId = 1, BusinessDate = new DateOnly(2026, 5, 3), AmountGil = 5_000 },
            new() { Kind = LedgerEntryKind.HistoricalDay, FcId = 1, BusinessDate = new DateOnly(2026, 5, 7), AmountGil = 120_000 },
        ];

        var report = LedgerReports.Build(Fleet, entries, Utc, new DateRange(new DateOnly(2026, 5, 1), Today), PeriodGrouping.Day, Today, farming: farming);

        Assert.Equal(100_000 + 120_000 + 100_000 + 150_000 + 100_000 + 150_000, report.TotalGil);
    }
}
