using System.Text.Json;
using SubLedger.Core.Analytics;
using SubLedger.Core.Export;
using SubLedger.Core.Import;
using SubLedger.Core.Model;

namespace SubLedger.Tests;

public class RangePresetTests
{
    // Thursday 26 November 2026.
    private static readonly DateOnly Today = new(2026, 11, 26);
    private static readonly DateOnly First = new(2026, 4, 23);

    [Theory]
    [InlineData(RangePreset.Last7Days, "2026-11-20", "2026-11-26")]
    [InlineData(RangePreset.Last30Days, "2026-10-28", "2026-11-26")]
    [InlineData(RangePreset.Last90Days, "2026-08-29", "2026-11-26")]
    [InlineData(RangePreset.ThisWeek, "2026-11-23", "2026-11-26")]
    [InlineData(RangePreset.LastWeek, "2026-11-16", "2026-11-22")]
    [InlineData(RangePreset.ThisMonth, "2026-11-01", "2026-11-26")]
    [InlineData(RangePreset.LastMonth, "2026-10-01", "2026-10-31")]
    [InlineData(RangePreset.ThisQuarter, "2026-10-01", "2026-11-26")]
    [InlineData(RangePreset.LastQuarter, "2026-07-01", "2026-09-30")]
    [InlineData(RangePreset.ThisYear, "2026-01-01", "2026-11-26")]
    [InlineData(RangePreset.Lifetime, "2026-04-23", "2026-11-26")]
    public void Presets_cover_the_expected_dates(RangePreset preset, string from, string to)
    {
        var range = RangePresets.Resolve(preset, Today, First);

        Assert.Equal(DateOnly.Parse(from), range.From);
        Assert.Equal(DateOnly.Parse(to), range.To);
    }

    [Fact]
    public void Last_quarter_crosses_the_year_boundary()
    {
        var range = RangePresets.Resolve(RangePreset.LastQuarter, new DateOnly(2027, 2, 10), First);

        Assert.Equal(new DateOnly(2026, 10, 1), range.From);
        Assert.Equal(new DateOnly(2026, 12, 31), range.To);
    }

    [Fact]
    public void Lifetime_of_an_empty_ledger_is_just_today()
    {
        var range = RangePresets.Resolve(RangePreset.Lifetime, Today, null);

        Assert.Equal(1, range.Days);
    }
}

public class ReportTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);

    private static long At(int year, int month, int day, int hour = 12)
        => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Voyage V(long collected, long gil, uint sub = 1, string route = "OJ", params LootLine[] loot)
        => new() { FcId = 1, SubRegister = sub, SubName = $"Sub {sub}", Route = route, CollectedUnix = collected, TotalGil = gil, Loot = loot };

    private static LedgerEntry Manual(DateOnly date, long gil, LedgerEntryKind kind = LedgerEntryKind.HistoricalDay)
        => new() { Kind = kind, BusinessDate = date, AmountGil = gil };

    [Fact]
    public void A_report_counts_only_what_falls_in_its_range()
    {
        Voyage[] voyages = [V(At(2026, 5, 1), 100), V(At(2026, 5, 10), 200), V(At(2026, 5, 11), 300), V(At(2026, 5, 20), 400)];
        var range = new DateRange(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 15));

        var report = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Day, new DateOnly(2026, 5, 31));

        Assert.Equal(500, report.TotalGil);
        Assert.Equal(2, report.Voyages);
        Assert.Equal(2, report.RecordedDays);
        Assert.Equal(4, report.MissingDays);
        Assert.Equal(500 / 6, report.AveragePerDay);
        Assert.Equal(250, report.AveragePerRecordedDay);
        Assert.Equal(250, report.MedianDay);
        Assert.Equal(6, report.Periods.Count);
    }

    [Fact]
    public void Days_before_the_ledger_began_or_after_today_are_not_called_missing()
    {
        Voyage[] voyages = [V(At(2026, 5, 10), 600), V(At(2026, 5, 12), 300)];
        var range = new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));

        var report = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Day, today: new DateOnly(2026, 5, 12));

        // Only 10, 11 and 12 May are the ledger's to answer for.
        Assert.Equal(new DateRange(new DateOnly(2026, 5, 10), new DateOnly(2026, 5, 12)), report.Covered);
        Assert.Equal(1, report.MissingDays);
        Assert.Equal(300, report.AveragePerDay);
    }

    [Fact]
    public void A_zero_day_is_counted_separately_from_a_day_without_a_record()
    {
        var report = LedgerReports.Build(
            [V(At(2026, 5, 1), 500), V(At(2026, 5, 4), 700)],
            [Manual(new DateOnly(2026, 5, 2), 0)],
            Utc, new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 4)), PeriodGrouping.Day, new DateOnly(2026, 5, 4));

        Assert.Equal(3, report.RecordedDays);
        Assert.Equal(1, report.ZeroDays);
        Assert.Equal(1, report.MissingDays);
        Assert.Equal(0, report.WorstDay!.IncomeGil);
    }

    [Fact]
    public void Weeks_run_monday_to_sunday_and_are_cut_at_the_range_edges()
    {
        var voyages = Enumerable.Range(1, 30).Select(d => V(At(2026, 11, d), 1000)).ToArray();
        var range = new DateRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        var report = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Week, new DateOnly(2026, 11, 30));

        // 1 Nov 2026 is a Sunday: a one-day week, then four full weeks, then Monday 30 Nov alone.
        Assert.Equal([1, 7, 7, 7, 7, 1], report.Periods.Select(p => p.Days));
        Assert.Equal(new DateOnly(2026, 11, 2), report.Periods[1].From);
        Assert.Equal(new DateOnly(2026, 11, 8), report.Periods[1].To);
        Assert.Equal(7000, report.Periods[1].IncomeGil);
        Assert.Equal(30_000, report.Periods.Sum(p => p.IncomeGil));
    }

    [Fact]
    public void Months_and_quarters_group_and_label_correctly()
    {
        Voyage[] voyages = [V(At(2026, 9, 30), 100), V(At(2026, 10, 1), 200), V(At(2026, 11, 15), 300), V(At(2027, 1, 2), 400)];
        var range = new DateRange(new DateOnly(2026, 9, 1), new DateOnly(2027, 1, 31));

        var months = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Month, new DateOnly(2027, 1, 31)).Periods;
        var quarters = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Quarter, new DateOnly(2027, 1, 31)).Periods;

        Assert.Equal(["Sep", "Oct", "Nov", "Dec", "Jan"], months.Select(p => p.Label));
        Assert.Equal([100, 200, 300, 0, 400], months.Select(p => p.IncomeGil));
        Assert.Equal("2027", months[4].Group);
        Assert.Equal(["Q3", "Q4", "Q1"], quarters.Select(p => p.Label));
        Assert.Equal([100, 500, 400], quarters.Select(p => p.IncomeGil));
        Assert.Equal("Q4 2026", quarters[1].Title);
    }

    [Fact]
    public void Items_are_totalled_across_voyages_and_ranked_by_value()
    {
        var ring = new LootLine(15, 1, 22500, "Salvaged Ring", 3, false, 8000);
        var ruby = new LootLine(15, 0, 5187, "Ruby", 20, false, 2);
        Voyage[] voyages = [V(At(2026, 5, 1), 24_040, loot: [ruby, ring]), V(At(2026, 5, 2), 16_000, loot: [ring with { Quantity = 2 }])];

        var report = LedgerReports.Build(voyages, [], Utc, new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 2)), PeriodGrouping.Day, new DateOnly(2026, 5, 2));

        Assert.Equal(["Salvaged Ring", "Ruby"], report.ByItem.Select(i => i.Name));
        Assert.Equal(5, report.ByItem[0].Quantity);
        Assert.Equal(40_000, report.ByItem[0].TotalGil);
        Assert.Equal(40, report.ByItem[1].TotalGil);
    }

    [Fact]
    public void Income_without_voyages_behind_it_is_reported_as_such()
    {
        var report = LedgerReports.Build(
            [V(At(2026, 5, 2), 1000)],
            [Manual(new DateOnly(2026, 5, 1), 700), Manual(new DateOnly(2026, 5, 2), 50, LedgerEntryKind.Adjustment)],
            Utc, new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 2)), PeriodGrouping.Day, new DateOnly(2026, 5, 2));

        Assert.Equal(1750, report.TotalGil);
        Assert.Equal(750, report.ManualGil);
    }

    [Fact]
    public void A_rolling_average_smooths_and_starts_with_what_it_has()
    {
        var average = LedgerReports.RollingAverage([700, 0, 0, 0, 0, 0, 0, 700], 7);

        Assert.Equal([700, 350, 233, 175, 140, 116, 100, 100], average);
    }
}

public class HistoryImportPlannerTests
{
    private static readonly DateOnly Start = new(2026, 4, 23);

    private static SortedDictionary<DateOnly, DayTotal> Existing(params DayTotal[] days)
        => new(days.ToDictionary(d => d.Date));

    [Fact]
    public void Day_numbers_become_dates_from_the_starting_day()
    {
        var rows = HistoryParser.Parse("Day 1: 125k\nDay 2: 1.047m\nDay 5: 0").Rows;

        var plan = HistoryImportPlanner.Plan(rows, Start, []);

        Assert.Equal([new DateOnly(2026, 4, 23), new DateOnly(2026, 4, 24), new DateOnly(2026, 4, 27)], plan.Rows.Select(r => r.Date));
        Assert.All(plan.Rows, r => Assert.Equal(HistoryRowStatus.Counts, r.Status));
        Assert.Equal(1_172_000, plan.CountedGil);
    }

    [Fact]
    public void A_day_that_already_has_voyages_is_kept_on_file_but_not_counted()
    {
        var rows = HistoryParser.Parse("Day 1: 125k\nDay 2: 300k").Rows;
        var existing = Existing(new DayTotal(new DateOnly(2026, 4, 24)) { Voyages = 4, CapturedGil = 290_113 });

        var plan = HistoryImportPlanner.Plan(rows, Start, existing);

        Assert.Equal(HistoryRowStatus.Counts, plan.Rows[0].Status);
        Assert.Equal(HistoryRowStatus.KeptButNotCounted, plan.Rows[1].Status);
        Assert.Equal(2, plan.ImportCount);
        Assert.Equal(125_000, plan.CountedGil);
        Assert.Equal(300_000, plan.OverlapGil);
    }

    [Fact]
    public void Importing_the_same_list_again_adds_nothing()
    {
        var rows = HistoryParser.Parse("Day 1: 125k").Rows;
        var existing = Existing(new DayTotal(Start) { HasManual = true, ManualGil = 125_000 });

        var plan = HistoryImportPlanner.Plan(rows, Start, existing);

        Assert.Equal(HistoryRowStatus.AlreadyImported, plan.Rows[0].Status);
        Assert.Equal(0, plan.ImportCount);
    }

    [Fact]
    public void When_a_day_is_listed_twice_the_later_line_stands()
    {
        var rows = HistoryParser.Parse("Day 3: 100k\nDay 3: 140k").Rows;

        var plan = HistoryImportPlanner.Plan(rows, Start, []);

        Assert.Equal(HistoryRowStatus.RepeatedInFile, plan.Rows[0].Status);
        Assert.Equal(HistoryRowStatus.Counts, plan.Rows[1].Status);
        Assert.Equal(140_000, plan.CountedGil);
    }

    [Fact]
    public void An_import_lands_in_the_ledger_and_can_be_undone_as_a_whole()
    {
        using var ledger = new TempLedger();
        var rows = HistoryParser.Parse("Day 1: 125k\nDay 2: 1.047m").Rows;
        var plan = HistoryImportPlanner.Plan(rows, Start, []);

        ledger.Store.AddEntries(HistoryImportPlanner.ToEntries(plan, ledger.Store, TempLedger.Fc, "my-list.txt", "batch-1"));
        ledger.Restart();

        var calendar = new BusinessCalendar(TimeZoneInfo.Utc, 0);
        var snapshot = ledger.Store.Snapshot();
        var days = LedgerAnalytics.Daily(snapshot.Voyages, snapshot.Entries, calendar);
        Assert.Equal(1_172_000, days.Values.Sum(d => d.IncomeGil));
        Assert.All(snapshot.Entries, e => Assert.Equal(TempLedger.Fc, e.FcId));

        Assert.Equal(2, ledger.Store.DeleteBatch("batch-1"));
        ledger.Restart();
        Assert.Empty(ledger.Store.Snapshot().Entries);
    }
}

public class ExportTests
{
    private static readonly BusinessCalendar Utc = new(TimeZoneInfo.Utc, 0);

    private static ExportContext Context() => new(Utc, new Dictionary<ulong, string> { [1] = "Starlit, Inc." }, new FakeSectors(), "9.9.9.9");

    private static Voyage Sample() => new()
    {
        Id = "abc",
        FcId = 1,
        SubRegister = 7,
        SubName = "Deep \"Blue\"",
        Route = "OJ",
        CollectedUnix = new DateTimeOffset(2026, 5, 5, 9, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        TotalGil = 1_024_040,
        Loot =
        [
            new LootLine(FakeSectors.O, 0, 5187, "Ruby", 20, true, 3),
            new LootLine(FakeSectors.J, 1, 22507, "Extravagant Salvaged Necklace", 2, false, 34_500),
        ],
    };

    [Fact]
    public void Voyage_csv_quotes_awkward_text_and_keeps_numbers_plain()
    {
        var lines = LedgerExport.VoyagesCsv([Sample()], Context()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("voyage_id,ledger_day,", lines[0]);
        Assert.Equal("abc,2026-05-05,2026-05-05 09:30:00,2026-05-05T09:30:00Z,\"Starlit, Inc.\",\"Deep \"\"Blue\"\"\",OJ,1024040,22,,,recorded by SubLedger", lines[1]);
    }

    [Fact]
    public void Loot_csv_has_one_row_per_stack()
    {
        var lines = LedgerExport.LootCsv([Sample()], Context()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",O,Sector 15,5187,Ruby,20,yes,3,60", lines[1]);
        Assert.EndsWith(",J,Sector 10,22507,Extravagant Salvaged Necklace,2,no,34500,69000", lines[2]);
    }

    [Fact]
    public void Daily_csv_lists_every_day_and_says_which_have_no_record()
    {
        var days = LedgerAnalytics.Daily([Sample()], [new LedgerEntry { Kind = LedgerEntryKind.HistoricalDay, BusinessDate = new DateOnly(2026, 5, 7), AmountGil = 0 }], Utc);

        var lines = LedgerExport.DailyCsv(days, new DateRange(new DateOnly(2026, 5, 5), new DateOnly(2026, 5, 7))).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Equal("2026-05-05,recorded,1024040,1,1024040,,,0", lines[1]);
        Assert.Equal("2026-05-06,no record,,0,,,,", lines[2]);
        Assert.Equal("2026-05-07,recorded,0,0,0,0,yes,0", lines[3]);
    }

    [Fact]
    public void Text_that_looks_like_a_formula_is_defused()
    {
        Assert.Equal("'=SUM(A1)", LedgerExport.Cell("=SUM(A1)"));
        Assert.Equal("-1500", LedgerExport.Cell("-1500"));
        Assert.Equal("Nautilus", LedgerExport.Cell("Nautilus"));
    }

    [Fact]
    public void Json_is_valid_and_carries_exact_values()
    {
        Voyage[] voyages = [Sample()];
        var range = new DateRange(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 6));
        var report = LedgerReports.Build(voyages, [], Utc, range, PeriodGrouping.Day, new DateOnly(2026, 5, 6));
        var days = LedgerAnalytics.Daily(voyages, [], Utc);

        using var json = JsonDocument.Parse(LedgerExport.Json(report, voyages, days, Context(), DateTimeOffset.UnixEpoch));
        var root = json.RootElement;

        Assert.Equal("9.9.9.9", root.GetProperty("version").GetString());
        Assert.Equal(1_024_040, root.GetProperty("summary").GetProperty("totalGil").GetInt64());
        Assert.Equal(3, root.GetProperty("days").GetArrayLength());
        Assert.False(root.GetProperty("days")[0].GetProperty("recorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("days")[0].GetProperty("incomeGil").ValueKind);
        var voyage = root.GetProperty("voyages")[0];
        Assert.Equal("0000000000000001", voyage.GetProperty("freeCompanyId").GetString());
        Assert.Equal(69_000, voyage.GetProperty("loot")[1].GetProperty("totalGil").GetInt64());
    }
}
