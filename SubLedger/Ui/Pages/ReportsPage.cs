using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Model;

namespace SubLedger.Ui.Pages;

/// <summary>Income for any stretch of time: totals, how it moved, where it came from, and a way to take it elsewhere.</summary>
internal sealed class ReportsPage
{
    private enum ChartMode
    {
        PerPeriod,
        RunningTotal,
    }

    private enum Breakdown
    {
        Items,
        Submarines,
        Routes,
        Companies,
    }

    /// <summary>One Free Company's figures for the chosen period, for side-by-side comparison.</summary>
    private sealed record CompanyRow(string Name, long Income, int Voyages, long AveragePerDay, long PerVoyage, long Per24Hours, int RecordedDays, int CoveredDays, long Net);

    private static readonly string[] DateFormats = ["yyyy-M-d", "M/d/yyyy", "d MMM yyyy"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Plugin plugin;

    private RangePreset preset = RangePreset.Last30Days;
    private DateOnly customFrom;
    private DateOnly customTo;
    private string fromText = string.Empty;
    private string toText = string.Empty;
    private bool datesInvalid;
    private PeriodGrouping grouping = PeriodGrouping.Day;
    private bool groupingChosen;
    private ChartMode chartMode = ChartMode.PerPeriod;
    private Breakdown breakdown = Breakdown.Items;

    private (long Version, FcFilter Fc, string Zone, int Cutoff, DateOnly Today, DateRange Range, PeriodGrouping Grouping, FarmingPeriod? Farming) cacheKey;
    private (long Version, FcFilter Fc, string Zone, int Cutoff, FarmingPeriod? Farming) daysKey;
    private List<CompanyRow> companies = [];
    private RangeReport report = new();
    private SortedDictionary<DateOnly, DayTotal> days = [];
    private List<Voyage> voyagesInRange = [];
    private DateOnly? firstDay;

    /// <summary>Set while a farming preset is chosen: the levelling days before it are left out.</summary>
    private FarmingPeriod? farming;

    public ReportsPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private string Gil(long value) => GilFormat.Format(value, plugin.Config.AbbreviateGil);

    /// <summary>Daily bars stop being readable past a few months, so longer ranges start out grouped.</summary>
    private static PeriodGrouping SuggestedGrouping(int rangeDays) => rangeDays switch
    {
        <= 92 => PeriodGrouping.Day,
        <= 400 => PeriodGrouping.Week,
        _ => PeriodGrouping.Month,
    };

    private void Refresh()
    {
        var calendar = plugin.Calendar;
        var today = calendar.Today(DateTimeOffset.Now);
        var snapshot = plugin.Counted();
        var fc = plugin.Scope;
        farming = RangePresets.FarmingRule(preset) is { } rule ? plugin.Farming(rule) : null;

        // The ledger's first day is needed before the range (for "Lifetime"), so the daily picture comes first.
        var dk = (snapshot.Version, fc, calendar.Zone.Id, calendar.CutoffMinutes, farming);
        if (dk != daysKey)
        {
            daysKey = dk;
            IReadOnlyList<Voyage> voyages = snapshot.Voyages;
            IReadOnlyList<LedgerEntry> entries = snapshot.Entries;
            if (farming is not null)
                (voyages, entries) = farming.Apply(voyages, entries, calendar);

            days = LedgerAnalytics.Daily(voyages, entries, calendar, fc);
            if (farming is not null)
            {
                firstDay = farming.FirstFor(fc);
            }
            else
            {
                firstDay = days.Count > 0 ? days.Keys.First() : null;

                // "Lifetime" also takes in spending from before the first voyage, such as the parts and the plot.
                foreach (var e in snapshot.Entries)
                {
                    if (!e.Deleted && ExpenseCategories.IsCost(e.Kind) && fc.Includes(e.FcId) && (firstDay is null || e.BusinessDate < firstDay))
                        firstDay = e.BusinessDate;
                }
            }
        }

        var range = preset == RangePreset.Custom
            ? new DateRange(customFrom, customTo)
            : RangePresets.Resolve(preset, today, firstDay);
        if (!groupingChosen)
            grouping = SuggestedGrouping(range.Days);

        var key = (snapshot.Version, fc, calendar.Zone.Id, calendar.CutoffMinutes, today, range, grouping, farming);
        if (key == cacheKey)
            return;

        cacheKey = key;
        report = LedgerReports.Build(snapshot.Voyages, snapshot.Entries, calendar, range, grouping, today, fc, farming);
        voyagesInRange = [.. snapshot.Voyages.Where(v => fc.Includes(v.FcId) && range.Contains(calendar.DateOf(v.CollectedUnix))
            && (farming is null || farming.Covers(v, calendar.DateOf(v.CollectedUnix))))];

        // Each Free Company in view on its own, when there is more than one to compare.
        companies = [];
        var inView = snapshot.FreeCompanies.Where(c => fc.Includes(c.FcId)).ToList();
        if (inView.Count > 1)
        {
            var rates = LedgerAnalytics.ByFreeCompany(voyagesInRange, id => id.ToString("X", Inv), fc).ToDictionary(g => g.Key, g => g.GilPer24Hours);
            foreach (var company in inView)
            {
                var r = LedgerReports.Build(snapshot.Voyages, snapshot.Entries, calendar, range, grouping, today, company.FcId, farming);
                companies.Add(new CompanyRow(company.DisplayName, r.TotalGil, r.Voyages, r.AveragePerDay, r.AveragePerVoyage,
                    rates.GetValueOrDefault(company.FcId.ToString("X", Inv)), r.RecordedDays, r.Covered?.Days ?? 0, r.NetGil));
            }

            companies.Sort((a, b) => b.Income.CompareTo(a.Income));
        }

        if (breakdown == Breakdown.Companies && companies.Count < 2)
            breakdown = Breakdown.Items;

        if (preset != RangePreset.Custom)
        {
            fromText = range.From.ToString("yyyy-MM-dd", Inv);
            toText = range.To.ToString("yyyy-MM-dd", Inv);
            datesInvalid = false;
        }
    }

    public void Draw()
    {
        Refresh();
        var assets = plugin.Assets;
        var snapshot = plugin.Counted();
        var scope = plugin.ScopeLabel();
        Widgets.PageHeader("Reports", $"{scope} · {report.Range} · {report.Range.Days} {(report.Range.Days == 1 ? "day" : "days")}", assets);

        DrawFilters(assets);
        Widgets.VSpace(6f);

        if (report.RecordedDays == 0)
        {
            Widgets.BeginPanel();
            Widgets.Text("Nothing recorded in this period", Theme.Text, assets.Heading);
            Widgets.Text("Choose a different range above. Days before the ledger's first record, and days still to come, have nothing to show.", Theme.TextSoft);
            Widgets.EndPanel();
            return;
        }

        DrawTiles(assets);
        Widgets.VSpace(6f);
        DrawChart(assets);
        Widgets.VSpace(6f);
        DrawBreakdown(assets);
        Widgets.VSpace(10f);
        if (companies.Count > 1)
        {
            DrawCompanies(assets);
            Widgets.VSpace(10f);
        }
        DrawPeriodTable(assets);
        Widgets.VSpace(10f);
        DrawExport(assets);
        Widgets.VSpace(8f);
    }

    /// <summary>One row of filters above everything they affect.</summary>
    private void DrawFilters(UiAssets assets)
    {
        var startX = ImGui.GetCursorPosX();
        var limit = startX + ImGui.GetContentRegionAvail().X;
        var gap = Theme.S(8f);
        var x = startX;

        var drawn = 0;
        var scope = plugin.Scope;
        foreach (var option in RangePresets.Offered)
        {
            // The farming presets only appear once a Free Company in view has run a farming route.
            if (RangePresets.FarmingRule(option) is { } rule && plugin.Farming(rule).FirstFor(scope) is null && preset != option)
                continue;

            var label = RangePresets.Label(option);
            var width = Widgets.PillSize(label, assets, dot: false).X + Theme.S(6f);
            if (drawn++ > 0)
            {
                // Flow onto a new line when the next pill would run past the edge.
                if (x + width > limit)
                    x = startX;
                else
                    ImGui.SameLine(0, gap);
            }

            if (Widgets.FilterPill(label, Theme.Gold, preset == option, assets))
            {
                preset = option;
                groupingChosen = false;
            }

            if (option == RangePreset.FarmingFirstSub)
                Widgets.Tooltip($"Each submarine counts only from its own first voyage on {RouteWords()}, so levelling voyages are left out, including those of submarines still levelling while another farms.");
            else if (option == RangePreset.FarmingAllSubs)
                Widgets.Tooltip($"From the first day all of your submarines had come back from {RouteWords()}, so the levelling days before it are left out.\nA submarine that has never run one is not waited for.");

            x += width + gap;
        }

        Widgets.VSpace(2f);

        ImGui.AlignTextToFramePadding();
        Widgets.Text("From", Theme.TextDim);
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(Theme.S(124f));
        var edited = ImGui.InputTextWithHint("##from", "yyyy-mm-dd", ref fromText, 16);
        ImGui.SameLine(0, gap);
        ImGui.AlignTextToFramePadding();
        Widgets.Text("to", Theme.TextDim);
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(Theme.S(124f));
        edited |= ImGui.InputTextWithHint("##to", "yyyy-mm-dd", ref toText, 16);

        if (edited)
        {
            if (DateOnly.TryParseExact(fromText.Trim(), DateFormats, Inv, DateTimeStyles.None, out var from)
                && DateOnly.TryParseExact(toText.Trim(), DateFormats, Inv, DateTimeStyles.None, out var to)
                && from <= to)
            {
                customFrom = from;
                customTo = to;
                preset = RangePreset.Custom;
                groupingChosen = false;
                datesInvalid = false;
            }
            else
            {
                datesInvalid = true;
            }
        }

        if (datesInvalid)
        {
            ImGui.SameLine(0, gap);
            ImGui.AlignTextToFramePadding();
            Widgets.Text("Enter both dates as year-month-day, earliest first.", Theme.Rose);
        }

        ImGui.SameLine(0, Theme.S(26f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text("Group by", Theme.TextDim);
        foreach (var option in new[] { PeriodGrouping.Day, PeriodGrouping.Week, PeriodGrouping.Month, PeriodGrouping.Quarter })
        {
            ImGui.SameLine(0, gap);
            if (Widgets.FilterPill(option.ToString(), Theme.Teal, grouping == option, assets, "group" + option))
            {
                grouping = option;
                groupingChosen = true;
            }
        }

        if (farming is not null)
        {
            Widgets.VSpace(2f);
            Widgets.TextWrapped(FarmingNote(), Theme.TextDim);
        }
    }

    private string RouteWords()
    {
        var routes = FarmingPeriod.ParseRoutes(plugin.Config.FarmingRoutes);
        return routes.Count switch
        {
            0 => "a farming route",
            1 => routes[0],
            _ => string.Join(", ", routes.Take(routes.Count - 1)) + " or " + routes[^1],
        };
    }

    /// <summary>Which days a farming preset counts, in words, with each Free Company's start.</summary>
    private string FarmingNote()
    {
        if (farming is null)
            return string.Empty;

        var scope = plugin.Scope;
        var names = plugin.Counted().FreeCompanies.ToDictionary(c => c.FcId, c => c.DisplayName);
        var starts = farming.Starts.Where(kv => kv.Key != 0 && scope.Includes(kv.Key)).OrderBy(kv => kv.Value).ToList();
        var rule = farming.Rule == FarmingStart.WholeFleet
            ? $"the first day all of the submarines had come back from {RouteWords()}"
            : $"the first day a submarine came back from {RouteWords()}, each submarine only from its own first farming voyage";

        if (starts.Count == 0)
            return farming.IsEmpty
                ? $"No submarine has come back from {RouteWords()} yet, so there are no farming days to show. The farming routes are set in Settings."
                : "None of the Free Companies in view has farmed yet.";

        var when = starts.Count == 1 && names.Count <= 1
            ? starts[0].Value.ToString("d MMM yyyy", Inv)
            : string.Join(", ", starts.Select(kv => $"{names.GetValueOrDefault(kv.Key, "Free Company")} {kv.Value.ToString("d MMM yyyy", Inv)}"));
        return $"Farming days only: counting from {rule} ({when}). Levelling days before that are left out. The farming routes are set in Settings.";
    }

    private void DrawTiles(UiAssets assets)
    {
        var best = report.BestDay;
        var daysDetail = report.MissingDays == 0 && report.ZeroDays == 0
            ? "Every day recorded"
            : $"{report.ZeroDays} at zero, {report.MissingDays} without a record";
        var covered = report.Covered?.Days ?? 0;

        var tiles = new List<(string Label, string Value, string? Detail, Vector4 Accent, string Tip)>
        {
            ("Income", Gil(report.TotalGil), $"{report.Voyages.ToString("N0", Inv)} {(report.Voyages == 1 ? "voyage" : "voyages")}", Theme.Gold,
                $"{GilFormat.Exact(report.TotalGil)} gil in this period."
                + (report.ManualGil != 0 ? $"\n{GilFormat.Exact(report.ManualGil)} gil of it comes from hand-kept totals and corrections rather than recorded voyages." : string.Empty)),
            ("Average per day", Gil(report.AveragePerDay), $"{GilFormat.Short(report.AveragePerRecordedDay)} on days with a record", Theme.Teal,
                $"{GilFormat.Exact(report.AveragePerDay)} gil a day across all {covered} days the ledger covers in this period.\n{GilFormat.Exact(report.AveragePerRecordedDay)} gil a day counting only the {report.RecordedDays} days with a record."),
            ("Typical day", Gil(report.MedianDay), "Half of recorded days earned more", Theme.Blue,
                $"The median recorded day: {GilFormat.Exact(report.MedianDay)} gil. Unlike the average, one unusually good or bad day barely moves it."),
            ("Best day", best is null ? "-" : Gil(best.IncomeGil), best?.Date.ToString("ddd d MMM yyyy", Inv), Theme.Amber,
                best is null ? string.Empty : $"{GilFormat.Exact(best.IncomeGil)} gil."
                + (report.WorstDay is { } worst ? $"\nLowest recorded day: {GilFormat.Exact(worst.IncomeGil)} gil on {worst.Date.ToString("d MMM yyyy", Inv)}." : string.Empty)),
            ("Per voyage", Gil(report.AveragePerVoyage), report.ByRoute.Count > 0 ? $"Mostly route {report.ByRoute[0].Label}" : null, Theme.Violet,
                $"{GilFormat.Exact(report.AveragePerVoyage)} gil per voyage on average."),
            ("Days recorded", $"{report.RecordedDays} of {covered}", daysDetail, Theme.Mint,
                $"{report.RecordedDays} days have a record.\n{report.ZeroDays} of those earned nothing.\n{report.MissingDays} days have no record at all: nothing was collected, or it was not seen."),
        };

        // Spending in the period adds two tiles: six become eight, laid out four by two when there is room.
        if (report.RunningCostsGil != 0 || report.InvestedGil != 0)
        {
            tiles.Insert(1, ("After running costs", Gil(report.NetGil), $"{GilFormat.Short(report.RunningCostsGil)} spent on running", Theme.Violet,
                $"{GilFormat.Exact(report.TotalGil)} gil earned, less {GilFormat.Exact(report.RunningCostsGil)} gil of running costs dated in this period: {GilFormat.Exact(report.NetGil)} gil."));
            tiles.Add(("Invested", Gil(report.InvestedGil), "One-off purchases in this period", Theme.Mint,
                $"{GilFormat.Exact(report.InvestedGil)} gil of parts, housing and other investments dated in this period. The Expenses page shows the payback."));
        }

        var gap = Theme.S(10f);
        var available = ImGui.GetContentRegionAvail().X;
        var wide = tiles.Count == 8 ? Theme.S(720f) : Theme.S(860f);
        var perRow = available >= wide ? (tiles.Count == 8 ? 4 : 3) : 2;
        var width = MathF.Floor((available - gap * (perRow - 1)) / perRow);
        var size = new Vector2(width, Theme.S(104f));

        for (var i = 0; i < tiles.Count; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, gap);
            var t = tiles[i];
            Widgets.StatTile(t.Label, t.Value, t.Detail, t.Accent, size, assets, t.Tip);
        }

        // Fuel is paid in company credits, not gil, so it sits beside the figures rather than in them.
        var tanks = Fuel.Tanks(voyagesInRange, plugin.Sectors);
        if (tanks > 0)
        {
            Widgets.VSpace(2f);
            Widgets.Text($"Fuel: {tanks.ToString("N0", Inv)} ceruleum tanks used in this period, {(tanks * Fuel.CreditsPerTank).ToString("N0", Inv)} company credits.", Theme.TextDim);
        }
    }

    private void DrawChart(UiAssets assets)
    {
        Widgets.BeginCard("##reportChart", new Vector2(0, Theme.S(306f)));

        var unit = grouping.ToString().ToLowerInvariant();
        Widgets.Text(chartMode == ChartMode.PerPeriod ? $"Income per {unit}" : "Running total", Theme.Text, assets.Heading);

        var modes = new[] { (ChartMode.PerPeriod, $"Per {unit}"), (ChartMode.RunningTotal, "Running total") };
        var pillsWidth = modes.Sum(m => Widgets.PillSize(m.Item2, assets, dot: false).X + Theme.S(6f)) + Theme.S(8f);
        ImGui.SameLine(ImGui.GetWindowWidth() - pillsWidth - Theme.S(14f));
        for (var i = 0; i < modes.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.S(8f));
            if (Widgets.FilterPill(modes[i].Item2, Theme.Gold, chartMode == modes[i].Item1, assets, "mode" + modes[i].Item1))
                chartMode = modes[i].Item1;
        }

        Widgets.VSpace(2f);

        var periods = report.Periods;
        var size = ImGui.GetContentRegionAvail();

        if (chartMode == ChartMode.RunningTotal)
        {
            var points = new List<ChartBar>(periods.Count);
            long running = 0;
            foreach (var p in periods)
            {
                running += p.IncomeGil;
                points.Add(new ChartBar(p.Label, p.Group, running, false, $"Through {p.Title}", $"{GilFormat.Exact(p.IncomeGil)} gil added in this {unit}", false));
            }

            BarChart.DrawArea(size, points, assets);
        }
        else
        {
            var bars = new List<ChartBar>(periods.Count);
            var values = new List<long>(periods.Count);
            foreach (var p in periods)
            {
                var detail = p.Voyages == 1 ? "1 voyage" : $"{p.Voyages.ToString("N0", Inv)} voyages";
                if (grouping != PeriodGrouping.Day && p.Days > 0)
                    detail += $"\n{p.RecordedDays} of {p.Days} days recorded";
                bars.Add(new ChartBar(p.Label, p.Group, Math.Max(0, p.IncomeGil), p.RecordedDays == 0, p.Title, detail, false));
                values.Add(Math.Max(0, p.IncomeGil));
            }

            // A week of smoothing shows the direction through the day-to-day swing. Same axis: both are gil per day.
            if (grouping == PeriodGrouping.Day && bars.Count >= 14)
                BarChart.Draw(size, bars, assets, line: LedgerReports.RollingAverage(values, 7), barLegend: "Income per day", lineLegend: "7-day average");
            else
                BarChart.Draw(size, bars, assets);
        }

        Widgets.EndCard();
    }

    private void DrawBreakdown(UiAssets assets)
    {
        Widgets.BeginPanel();
        Widgets.Text("Where the gil came from", Theme.Text, assets.Heading);

        var options = companies.Count > 1
            ? new[] { (Breakdown.Companies, "Free Companies"), (Breakdown.Items, "Items"), (Breakdown.Submarines, "Submarines"), (Breakdown.Routes, "Routes") }
            : new[] { (Breakdown.Items, "Items"), (Breakdown.Submarines, "Submarines"), (Breakdown.Routes, "Routes") };
        var pillsWidth = options.Sum(o => Widgets.PillSize(o.Item2, assets, dot: false).X + Theme.S(6f)) + Theme.S(8f) * (options.Length - 1);
        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(Widgets.PanelRight - pillsWidth, ImGui.GetCursorScreenPos().Y));
        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.S(8f));
            if (Widgets.FilterPill(options[i].Item2, Theme.Gold, breakdown == options[i].Item1, assets, "breakdown" + options[i].Item1))
                breakdown = options[i].Item1;
        }

        Widgets.VSpace(4f);

        const int Shown = 8;
        var rows = new List<RankedItem>();
        switch (breakdown)
        {
            case Breakdown.Items:
                rows.AddRange(report.ByItem.Take(Shown).Select(i => new RankedItem(i.Name, i.TotalGil, $"{i.Quantity.ToString("N0", Inv)} brought back")));
                if (report.ByItem.Count > Shown)
                {
                    var rest = report.ByItem.Skip(Shown).ToList();
                    rows.Add(new RankedItem($"Everything else ({rest.Count} items)", rest.Sum(i => i.TotalGil), string.Join(", ", rest.Take(6).Select(i => i.Name)) + (rest.Count > 6 ? " and more" : string.Empty)));
                }

                break;
            case Breakdown.Submarines:
                rows.AddRange(report.BySubmarine.Take(Shown + 1).Select(g => new RankedItem(g.Label, g.TotalGil, GroupDetail(g))));
                break;
            case Breakdown.Companies:
                rows.AddRange(companies.Select(c => new RankedItem(c.Name, c.Income, $"{c.Voyages.ToString("N0", Inv)} voyages, {GilFormat.Exact(c.AveragePerDay)} gil a day on average")));
                break;
            default:
                rows.AddRange(report.ByRoute.Take(Shown).Select(g => new RankedItem(g.Label, g.TotalGil, GroupDetail(g))));
                if (report.ByRoute.Count > Shown)
                {
                    var rest = report.ByRoute.Skip(Shown).ToList();
                    rows.Add(new RankedItem($"Everything else ({rest.Count} routes)", rest.Sum(g => g.TotalGil), $"{rest.Sum(g => g.Voyages).ToString("N0", Inv)} voyages"));
                }

                break;
        }

        if (report.ManualGil > 0 && breakdown != Breakdown.Companies)
            rows.Add(new RankedItem("Hand-kept totals and corrections", report.ManualGil, "Income entered by hand, with no voyage detail behind it"));

        BarChart.DrawRanked(rows, Math.Max(1, report.TotalGil), assets);
        Widgets.EndPanel();
    }

    private static string GroupDetail(GroupStats g)
    {
        var text = $"{g.Voyages.ToString("N0", Inv)} {(g.Voyages == 1 ? "voyage" : "voyages")}, {GilFormat.Exact(g.AveragePerVoyage)} gil each on average";
        return g.GilPer24Hours == 0 ? text : text + $"\n{GilFormat.Exact(g.GilPer24Hours)} gil per 24 hours";
    }

    /// <summary>The Free Companies in view, side by side, for the chosen period.</summary>
    private void DrawCompanies(UiAssets assets)
    {
        var rowHeight = ImGui.GetTextLineHeight() + Theme.S(12f);
        var hasCosts = report.RunningCostsGil != 0;
        Widgets.BeginCard("##companies", new Vector2(0, Theme.S(66f) + rowHeight * (companies.Count + 1)));
        Widgets.Text("Free Companies side by side", Theme.Text, assets.Heading);
        Widgets.VSpace(2f);

        var columns = hasCosts ? 7 : 6;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Theme.S(6f, 6f));
        if (ImGui.BeginTable("##companyTable", columns, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Free Company", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            for (var i = 1; i < columns; i++)
                ImGui.TableSetupColumn($"c{i}", ImGuiTableColumnFlags.WidthStretch, 1.2f);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Text("FREE COMPANY", Theme.TextDim, assets.Small);
            var headers = new List<string> { "INCOME", "PER DAY", "VOYAGES", "PER VOYAGE", "PER 24H" };
            if (hasCosts)
                headers.Insert(1, "AFTER COSTS");
            foreach (var header in headers)
            {
                ImGui.TableNextColumn();
                using (assets.Small.Push())
                    Widgets.TextRight(header, Theme.TextDim);
            }

            foreach (var c in companies)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Widgets.Text(c.Name, Theme.Text);
                ImGui.TableNextColumn();
                Widgets.TextRight(GilFormat.Short(c.Income), Theme.Gold);
                Widgets.Tooltip($"{GilFormat.Exact(c.Income)} gil, {c.RecordedDays} of {c.CoveredDays} days recorded");
                if (hasCosts)
                {
                    ImGui.TableNextColumn();
                    Widgets.TextRight(GilFormat.Short(c.Net), Theme.TextSoft);
                    Widgets.Tooltip($"{GilFormat.Exact(c.Net)} gil after running costs");
                }

                ImGui.TableNextColumn();
                Widgets.TextRight(GilFormat.Short(c.AveragePerDay), Theme.TextSoft);
                ImGui.TableNextColumn();
                Widgets.TextRight(c.Voyages.ToString("N0", Inv), Theme.TextSoft);
                ImGui.TableNextColumn();
                Widgets.TextRight(GilFormat.Short(c.PerVoyage), Theme.TextSoft);
                ImGui.TableNextColumn();
                Widgets.TextRight(c.Per24Hours == 0 ? "-" : GilFormat.Short(c.Per24Hours), Theme.Teal);
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleVar();
        Widgets.EndCard();
    }

    /// <summary>The chart's numbers as a table, newest period first.</summary>
    private void DrawPeriodTable(UiAssets assets)
    {
        var periods = report.Periods;
        var rowHeight = ImGui.GetTextLineHeight() + Theme.S(12f);
        var visible = Math.Min(periods.Count, 10);
        var scrolls = periods.Count > visible;
        Widgets.BeginCard("##periods", new Vector2(0, Theme.S(66f) + rowHeight * (visible + 1)),
            flags: scrolls ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        Widgets.Text($"By {grouping.ToString().ToLowerInvariant()}", Theme.Text, assets.Heading);
        Widgets.VSpace(2f);

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Theme.S(6f, 6f));
        if (ImGui.BeginTable("##periodTable", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Period", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("Income", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Voyages", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Days", ImGuiTableColumnFlags.WidthStretch, 1.6f);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Text("PERIOD", Theme.TextDim, assets.Small);
            foreach (var header in new[] { "INCOME (GIL)", "VOYAGES", "DAYS RECORDED" })
            {
                ImGui.TableNextColumn();
                using (assets.Small.Push())
                    Widgets.TextRight(header, Theme.TextDim);
            }

            for (var i = periods.Count - 1; i >= 0; i--)
            {
                var p = periods[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Widgets.Text(p.Title, Theme.TextSoft);
                ImGui.TableNextColumn();
                if (p.RecordedDays == 0)
                    Widgets.TextRight("no record", Theme.TextMuted);
                else
                    Widgets.TextRight(GilFormat.Exact(p.IncomeGil), Theme.Gold);
                ImGui.TableNextColumn();
                Widgets.TextRight(p.Voyages.ToString("N0", Inv), Theme.TextSoft);
                ImGui.TableNextColumn();
                Widgets.TextRight(p.Days == 0 ? "-" : $"{p.RecordedDays} of {p.Days}", p.MissingDays > 0 ? Theme.TextDim : Theme.TextSoft);
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleVar();
        Widgets.EndCard();
    }

    private void DrawExport(UiAssets assets)
    {
        Widgets.BeginPanel();
        Widgets.Text("Full report in your browser", Theme.Text, assets.Heading);
        Widgets.Text(
            "One page with every chart and table, its own date and Free Company filters, a light and a dark look, and nothing to install. "
            + "It is a single file you can keep, print or send to someone; it opens on the period chosen here.",
            Theme.TextDim);
        Widgets.VSpace(4f);
        if (Widgets.Button(plugin.ExportRunning ? "Building..." : "Open HTML report", FontAwesomeIcon.ExternalLinkAlt, Widgets.ButtonKind.Primary, !plugin.ExportRunning))
            plugin.OpenHtmlReport(report.Range, preset, grouping);
        Widgets.PanelDivider();

        Widgets.Text("Take this period elsewhere", Theme.Text, assets.Heading);
        Widgets.Text(
            "Spreadsheet files (CSV) open in Excel, Google Sheets and the like: one file of voyages, one of every item, one of daily totals. "
            + "The data file (JSON) holds the same in one document for other programs. Amounts are always exact gil.",
            Theme.TextDim);
        Widgets.VSpace(4f);

        var busy = plugin.ExportRunning;
        if (Widgets.Button("Export spreadsheets", FontAwesomeIcon.FileCsv, enabled: !busy))
            plugin.ExportCsv(report, voyagesInRange, days);
        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Export data file", FontAwesomeIcon.FileCode, enabled: !busy))
            plugin.ExportJson(report, voyagesInRange, days);
        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Open exports folder", FontAwesomeIcon.FolderOpen))
            plugin.OpenExportFolder();

        if (!string.IsNullOrEmpty(plugin.ExportStatus))
        {
            Widgets.VSpace(2f);
            Widgets.Text(plugin.ExportStatus, plugin.ExportStatus.StartsWith("Export failed", StringComparison.Ordinal) ? Theme.Rose : Theme.Mint);
        }

        Widgets.EndPanel();
    }
}
