using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Import;
using SubLedger.Core.Model;

namespace SubLedger.Tests;

public class HistoryParserTests
{
    [Theory]
    [InlineData("125k", 125_000)]
    [InlineData("1.047m", 1_047_000)]
    [InlineData("1.047M", 1_047_000)]
    [InlineData("1,250,000", 1_250_000)]
    [InlineData("1047312", 1_047_312)]
    [InlineData("7", 7)]
    [InlineData("0", 0)]
    [InlineData("980 gil", 980)]
    [InlineData("2.5b", 2_500_000_000)]
    [InlineData("0.5k", 500)]
    [InlineData("-12k", -12_000)]
    public void Amounts_are_read_exactly(string text, long expected)
    {
        Assert.True(HistoryParser.TryParseAmount(text, out var gil));
        Assert.Equal(expected, gil);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("12x")]
    public void Nonsense_amounts_are_refused(string text)
        => Assert.False(HistoryParser.TryParseAmount(text, out _));

    [Fact]
    public void Reads_the_day_list_format()
    {
        var result = HistoryParser.Parse("Day 1: 125k\nDay 2: 1.047m\n\nday 4 - 0\nDay 5 = 312,455");

        Assert.Empty(result.Problems);
        Assert.Equal([1, 2, 4, 5], result.Rows.Select(r => r.DayNumber!.Value));
        Assert.Equal([125_000, 1_047_000, 0, 312_455], result.Rows.Select(r => r.Gil));

        var start = new DateOnly(2026, 4, 23);
        Assert.Equal(new DateOnly(2026, 4, 23), result.Rows[0].ResolveDate(start));
        Assert.Equal(new DateOnly(2026, 4, 26), result.Rows[2].ResolveDate(start));
    }

    [Fact]
    public void Reads_a_two_column_spreadsheet_export()
    {
        var result = HistoryParser.Parse("Day,Amount\r\n1,125000\r\n2,\"1,047,312\"\r\n3\t980\r\n");

        Assert.Empty(result.Problems);
        Assert.Equal([125_000, 1_047_312, 980], result.Rows.Select(r => r.Gil));
    }

    [Fact]
    public void Reads_calendar_dates_as_well_as_day_numbers()
    {
        var result = HistoryParser.Parse("2026-04-23, 125k\n4/24/2026: 1.047m");

        Assert.Empty(result.Problems);
        Assert.Equal(new DateOnly(2026, 4, 23), result.Rows[0].Date);
        Assert.Equal(new DateOnly(2026, 4, 24), result.Rows[1].Date);
        Assert.Equal(1_047_000, result.Rows[1].Gil);
    }

    [Fact]
    public void Lines_it_cannot_read_are_reported_not_guessed()
    {
        var result = HistoryParser.Parse("Day 1: 125k\nDay 2: plenty\nwhat is this 5");

        Assert.Single(result.Rows);
        Assert.Equal(2, result.Problems.Count);
    }
}

public class AnalyticsTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);

    private static long At(int year, int month, int day, int hour = 12)
        => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Voyage V(long collected, long gil, uint sub = 1, string route = "OJ", ulong fc = 1)
        => new() { FcId = fc, SubRegister = sub, SubName = $"Sub {sub}", Route = route, CollectedUnix = collected, TotalGil = gil };

    private static LedgerEntry Manual(DateOnly date, long gil, LedgerEntryKind kind = LedgerEntryKind.HistoricalDay)
        => new() { Kind = kind, BusinessDate = date, AmountGil = gil };

    [Fact]
    public void The_daily_cutoff_moves_early_morning_collections_to_the_previous_day()
    {
        var sixAm = new BusinessCalendar(TimeZoneInfo.Utc, 6 * 60);

        Assert.Equal(new DateOnly(2026, 5, 4), sixAm.DateOf(At(2026, 5, 5, hour: 2)));
        Assert.Equal(new DateOnly(2026, 5, 5), sixAm.DateOf(At(2026, 5, 5, hour: 6)));
        Assert.Equal(new DateOnly(2026, 5, 5), Utc.DateOf(At(2026, 5, 5, hour: 2)));
    }

    [Fact]
    public void The_time_zone_decides_which_day_a_collection_belongs_to()
    {
        var tokyo = new BusinessCalendar(TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"), 0);

        // 20:00 UTC is 05:00 the next day in Tokyo.
        Assert.Equal(new DateOnly(2026, 5, 6), tokyo.DateOf(At(2026, 5, 5, hour: 20)));
    }

    [Fact]
    public void Captured_voyages_win_over_a_hand_kept_total_for_the_same_day()
    {
        var day = new DateOnly(2026, 5, 5);
        var days = LedgerAnalytics.Daily([V(At(2026, 5, 5), 100_000)], [Manual(day, 125_000)], Utc);

        Assert.True(days[day].ManualExcluded);
        Assert.Equal(100_000, days[day].IncomeGil);
    }

    [Fact]
    public void A_hand_kept_total_counts_on_days_without_captured_voyages()
    {
        var day = new DateOnly(2026, 5, 4);
        var days = LedgerAnalytics.Daily([V(At(2026, 5, 5), 100_000)], [Manual(day, 125_000)], Utc);

        Assert.False(days[day].ManualExcluded);
        Assert.Equal(125_000, days[day].IncomeGil);
    }

    [Fact]
    public void Corrections_always_apply()
    {
        var day = new DateOnly(2026, 5, 5);
        var days = LedgerAnalytics.Daily(
            [V(At(2026, 5, 5), 100_000)],
            [Manual(day, 125_000), Manual(day, -1_500, LedgerEntryKind.Adjustment)],
            Utc);

        Assert.Equal(98_500, days[day].IncomeGil);
    }

    [Fact]
    public void A_zero_day_is_recorded_and_a_day_with_no_record_is_missing()
    {
        var days = LedgerAnalytics.Daily(
            [V(At(2026, 5, 1), 50_000), V(At(2026, 5, 5), 70_000)],
            [Manual(new DateOnly(2026, 5, 3), 0)],
            Utc);

        var summary = LedgerAnalytics.Summarize(days, new DateOnly(2026, 5, 5));

        Assert.Equal(3, summary.RecordedDays);      // 1st, 3rd (zero), 5th
        Assert.Equal(2, summary.MissingDays);       // 2nd and 4th
        Assert.Equal(0, summary.WorstDay!.IncomeGil);
        Assert.Equal(new DateOnly(2026, 5, 3), summary.WorstDay.Date);
    }

    [Fact]
    public void Summary_totals_cover_the_right_windows()
    {
        var voyages = new List<Voyage>();
        for (var d = 1; d <= 10; d++)
            voyages.Add(V(At(2026, 5, d), d * 1000));

        var summary = LedgerAnalytics.Summarize(LedgerAnalytics.Daily(voyages, [], Utc), new DateOnly(2026, 5, 10));

        Assert.Equal(10_000, summary.TodayGil);
        Assert.Equal(9_000, summary.YesterdayGil);
        Assert.Equal(4_000 + 5_000 + 6_000 + 7_000 + 8_000 + 9_000 + 10_000, summary.Last7Gil);
        Assert.Equal(55_000, summary.Last30Gil);
        Assert.Equal(55_000, summary.LifetimeGil);
        Assert.Equal(5_500, summary.AveragePerDay30);   // ten days of history, not thirty
        Assert.Equal(5_500, summary.MedianDay30);
        Assert.Equal(10_000, summary.BestDay!.IncomeGil);
    }

    [Fact]
    public void Each_free_company_can_be_viewed_alone_or_combined()
    {
        Voyage[] voyages = [V(At(2026, 5, 5), 100_000, fc: 1), V(At(2026, 5, 5), 40_000, fc: 2)];
        var day = new DateOnly(2026, 5, 5);

        Assert.Equal(140_000, LedgerAnalytics.Daily(voyages, [], Utc)[day].IncomeGil);
        Assert.Equal(100_000, LedgerAnalytics.Daily(voyages, [], Utc, fcs: 1)[day].IncomeGil);
        Assert.Equal(40_000, LedgerAnalytics.Daily(voyages, [], Utc, fcs: 2)[day].IncomeGil);
    }

    [Fact]
    public void Rate_per_24_hours_is_measured_between_collections()
    {
        // One submarine, collected every 24 hours, 120k each time.
        Voyage[] voyages = [V(At(2026, 5, 1), 120_000), V(At(2026, 5, 2), 120_000), V(At(2026, 5, 3), 120_000)];

        var sub = Assert.Single(LedgerAnalytics.BySubmarine(voyages));
        var route = Assert.Single(LedgerAnalytics.ByRoute(voyages));

        Assert.Equal(360_000, sub.TotalGil);
        Assert.Equal(120_000, sub.AveragePerVoyage);
        Assert.Equal(120_000, sub.GilPer24Hours);
        Assert.Equal(120_000, route.GilPer24Hours);
    }

    [Fact]
    public void A_36_hour_route_earns_less_per_day_than_its_per_voyage_total_suggests()
    {
        var start = At(2026, 5, 1);
        Voyage[] voyages =
        [
            V(start, 300_000, route: "JORZ"),
            V(start + 36 * 3600, 300_000, route: "JORZ"),
            V(start + 72 * 3600, 300_000, route: "JORZ"),
        ];

        var route = Assert.Single(LedgerAnalytics.ByRoute(voyages));

        Assert.Equal(300_000, route.AveragePerVoyage);
        Assert.Equal(200_000, route.GilPer24Hours);
    }
}

public class GilFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(1_047_312, "1,047,312")]
    public void Exact_keeps_every_digit(long gil, string expected) => Assert.Equal(expected, GilFormat.Exact(gil));

    [Theory]
    [InlineData(980, "980")]
    [InlineData(9_999, "9,999")]
    [InlineData(125_000, "125k")]
    [InlineData(12_500, "12.5k")]
    [InlineData(999_999, "1m")]
    [InlineData(1_047_000, "1.05m")]
    [InlineData(50_418_510, "50.4m")]
    [InlineData(2_500_000_000, "2.5b")]
    public void Short_form_is_readable(long gil, string expected) => Assert.Equal(expected, GilFormat.Short(gil));
}
