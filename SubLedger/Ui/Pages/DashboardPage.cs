using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Import;
using SubLedger.Core.Model;

namespace SubLedger.Ui.Pages;

/// <summary>The headline numbers: what the submarines earned today, lately and in total, and where it came from.</summary>
internal sealed class DashboardPage
{
    private readonly Plugin plugin;

    private (long Version, ulong Fc, string Zone, int Cutoff, DateOnly Today) cacheKey;
    private SortedDictionary<DateOnly, DayTotal> days = [];
    private LedgerSummary summary = new();
    private List<GroupStats> bySubmarine = [];
    private List<GroupStats> byRoute = [];
    private sealed record SubLatest(string Name, string FreeCompany, IReadOnlyList<Voyage> Voyages);

    private List<SubLatest> latest = [];
    private int chartDays = 30;

    private DateTime trackerCheckedAt = DateTime.MinValue;
    private bool trackerFound;

    public DashboardPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private string Gil(long value) => GilFormat.Format(value, plugin.Config.AbbreviateGil);

    private void Refresh()
    {
        var calendar = plugin.Calendar;
        var today = calendar.Today(DateTimeOffset.Now);
        var snapshot = plugin.Store.Snapshot();
        var key = (snapshot.Version, plugin.Config.SelectedFreeCompany, calendar.Zone.Id, calendar.CutoffMinutes, today);
        if (key == cacheKey)
            return;

        cacheKey = key;
        var fc = plugin.Config.SelectedFreeCompany;
        days = LedgerAnalytics.Daily(snapshot.Voyages, snapshot.Entries, calendar, fc);
        summary = LedgerAnalytics.Summarize(days, today);
        bySubmarine = LedgerAnalytics.BySubmarine(snapshot.Voyages, fc);
        byRoute = LedgerAnalytics.ByRoute(snapshot.Voyages, fc);

        // The last five voyages of each submarine. Submarines that have not sailed for a long while (replaced or
        // retired) are left out so the row stays the current fleet.
        var shown = snapshot.Voyages.Where(v => fc == 0 || v.FcId == fc).ToList();
        var newest = shown.Count == 0 ? 0 : shown[0].CollectedUnix;
        var severalCompanies = shown.Select(v => v.FcId).Distinct().Count() > 1;
        latest = [.. shown
            .GroupBy(v => (v.FcId, v.SubRegister))
            .Where(g => g.First().CollectedUnix >= newest - 45L * 86400)
            .OrderBy(g => g.Key.FcId).ThenBy(g => g.Key.SubRegister)
            .Take(12)
            .Select(g => new SubLatest(
                g.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.SubName))?.SubName ?? "Submarine",
                severalCompanies ? snapshot.FreeCompanies.FirstOrDefault(c => c.FcId == g.Key.FcId)?.DisplayName ?? string.Empty : string.Empty,
                [.. g.Take(5)]))];
    }

    public void Draw()
    {
        Refresh();
        var assets = plugin.Assets;
        var snapshot = plugin.Store.Snapshot();
        var fc = snapshot.FreeCompanies.FirstOrDefault(c => c.FcId == plugin.Config.SelectedFreeCompany);
        var scope = fc?.DisplayName ?? (snapshot.FreeCompanies.Count > 1 ? "All Free Companies combined" : snapshot.FreeCompanies.FirstOrDefault()?.DisplayName ?? "Your submarines");

        Widgets.PageHeader("Dashboard", $"{scope} · income counts on the day it was collected", assets);

        if (plugin.LedgerError is not null)
        {
            Widgets.BeginPanel();
            Widgets.Text("The ledger could not be opened", Theme.Rose, assets.Heading);
            Widgets.Text($"Nothing is being recorded. {plugin.LedgerError}", Theme.TextSoft);
            Widgets.Text("The Console page has the details. Your ledger file and its backups have not been changed.", Theme.TextDim);
            Widgets.EndPanel(Theme.Mix(Theme.Card, Theme.Rose, 0.08f), Theme.Alpha(Theme.Rose, 0.5f), Theme.Rose);
            Widgets.VSpace(10f);
            return;
        }

        DrawTrackerOffer(assets);

        if (summary.RecordedDays == 0)
        {
            DrawEmptyState(assets);
            return;
        }

        DrawTiles(assets);
        Widgets.VSpace(6f);
        DrawChart(assets);
        Widgets.VSpace(6f);
        DrawBreakdowns(assets);
        Widgets.VSpace(6f);
        DrawLatest(assets);
        Widgets.VSpace(8f);
    }

    private void DrawTrackerOffer(UiAssets assets)
    {
        if (plugin.Config.SubmarineTrackerSync || plugin.Config.SubmarineTrackerOfferDismissed)
            return;

        // Looking at the disk every frame would be wasteful; every few seconds is plenty.
        if ((DateTime.UtcNow - trackerCheckedAt).TotalSeconds > 5)
        {
            trackerCheckedAt = DateTime.UtcNow;
            trackerFound = File.Exists(Path.Combine(plugin.SubmarineTrackerDirectory, SubmarineTrackerImporter.DatabaseFileName));
        }

        if (!trackerFound)
            return;

        Widgets.BeginPanel();
        Widgets.Text("Your SubmarineTracker history can be imported", Theme.Text, assets.Heading);
        Widgets.Text(
            "SubmarineTracker has been keeping a record of your past voyages. SubLedger can copy that history into the ledger, "
            + "exact to the gil, and keep checking it at startup so nothing is missed. SubmarineTracker's own file is only read, never changed.",
            Theme.TextSoft);
        Widgets.VSpace(4f);
        if (Widgets.Button(plugin.ImportRunning ? "Importing..." : "Import history", FontAwesomeIcon.FileImport, Widgets.ButtonKind.Primary, !plugin.ImportRunning))
        {
            plugin.Config.SubmarineTrackerSync = true;
            plugin.Config.Save();
            plugin.StartSubmarineTrackerImport();
        }

        ImGui.SameLine(0, Theme.S(10f));
        if (Widgets.Button("Not now"))
        {
            plugin.Config.SubmarineTrackerOfferDismissed = true;
            plugin.Config.Save();
        }

        Widgets.EndPanel(Theme.Mix(Theme.Card, Theme.Teal, 0.07f), Theme.Alpha(Theme.Teal, 0.45f), Theme.Teal);
        Widgets.VSpace(10f);
    }

    private void DrawEmptyState(UiAssets assets)
    {
        Widgets.BeginPanel();
        Widgets.Text("No voyages recorded yet", Theme.Text, assets.Heading);
        Widgets.Text(
            "Collect a submarine as you normally do, by hand or with your usual plugin, and it will appear here. "
            + "SubLedger only watches: it never clicks or sends anything.",
            Theme.TextSoft);
        Widgets.EndPanel();
    }

    private void DrawTiles(UiAssets assets)
    {
        var gap = Theme.S(10f);
        var available = ImGui.GetContentRegionAvail().X;
        var perRow = available >= Theme.S(720f) ? 4 : 2;
        var width = MathF.Floor((available - gap * (perRow - 1)) / perRow);
        var size = new Vector2(width, Theme.S(104f));

        var todayDetail = summary.TodayVoyages == 0
            ? $"Yesterday {GilFormat.Short(summary.YesterdayGil)}"
            : $"{Plural(summary.TodayVoyages, "voyage")} · yesterday {GilFormat.Short(summary.YesterdayGil)}";

        var tiles = new (string Label, string Value, string? Detail, Vector4 Accent, string Tip)[]
        {
            ("Today", Gil(summary.TodayGil), todayDetail, Theme.Gold, $"{GilFormat.Exact(summary.TodayGil)} gil collected today."),
            ("Last 7 days", Gil(summary.Last7Gil), $"{GilFormat.Short(summary.Last7Gil / 7)} a day", Theme.Teal, $"{GilFormat.Exact(summary.Last7Gil)} gil over the last 7 days, today included."),
            ("Last 30 days", Gil(summary.Last30Gil), $"This month {GilFormat.Short(summary.MonthToDateGil)}", Theme.Blue, $"{GilFormat.Exact(summary.Last30Gil)} gil over the last 30 days.\n{GilFormat.Exact(summary.MonthToDateGil)} gil so far this calendar month."),
            ("Lifetime", Gil(summary.LifetimeGil), summary.FirstDay is { } first ? $"Since {first.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : null, Theme.Violet, $"{GilFormat.Exact(summary.LifetimeGil)} gil in total."),
            ("Daily average", Gil(summary.AveragePerDay30), $"Typical day {GilFormat.Short(summary.MedianDay30)}", Theme.Mint, $"Average per day over the last 30 days: {GilFormat.Exact(summary.AveragePerDay30)} gil.\nTypical (median) recorded day: {GilFormat.Exact(summary.MedianDay30)} gil."),
            ("On pace for", Gil(summary.ProjectedMonthGil), $"{GilFormat.Short(summary.ProjectedYearGil)} a year", Theme.Gold, $"If the last 30 days' average holds: {GilFormat.Exact(summary.ProjectedMonthGil)} gil a month, {GilFormat.Exact(summary.ProjectedYearGil)} gil a year."),
            ("Best day", summary.BestDay is { } best ? Gil(best.IncomeGil) : "-", summary.BestDay?.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture), Theme.Amber, summary.BestDay is { } b ? $"{GilFormat.Exact(b.IncomeGil)} gil from {Plural(b.Voyages, "voyage")}." : string.Empty),
            ("Voyages", summary.LifetimeVoyages.ToString("N0", CultureInfo.InvariantCulture), summary.MissingDays == 0 ? $"{summary.RecordedDays} days recorded" : $"{summary.RecordedDays} days recorded, {summary.MissingDays} without", Theme.Teal, $"{summary.RecordedDays} days have a record.\n{summary.MissingDays} days since the first record have none: nothing was collected, or it was not seen."),
        };

        for (var i = 0; i < tiles.Length; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, gap);
            var t = tiles[i];
            Widgets.StatTile(t.Label, t.Value, t.Detail, t.Accent, size, assets, t.Tip);
        }
    }

    private void DrawChart(UiAssets assets)
    {
        var height = Theme.S(268f);
        Widgets.BeginCard("##chart", new Vector2(0, height));

        Widgets.Text("Daily income", Theme.Text, assets.Heading);
        var ranges = new[] { 14, 30, 90 };
        var pillsWidth = ranges.Sum(r => Widgets.PillSize($"{r} days", assets, dot: false).X + Theme.S(6f)) + Theme.S(8f) * (ranges.Length - 1);
        ImGui.SameLine(ImGui.GetWindowWidth() - pillsWidth - Theme.S(14f));
        for (var i = 0; i < ranges.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.S(8f));
            if (Widgets.FilterPill($"{ranges[i]} days", Theme.Gold, chartDays == ranges[i], assets))
                chartDays = ranges[i];
        }

        Widgets.VSpace(2f);

        var today = summary.Today;
        var bars = new List<ChartBar>(chartDays);
        for (var offset = chartDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var label = date.Day.ToString(CultureInfo.InvariantCulture);
            var month = date.ToString("MMM", CultureInfo.InvariantCulture);
            var title = date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

            if (days.TryGetValue(date, out var day))
            {
                var detail = day.Voyages > 0 ? Plural(day.Voyages, "voyage") : day.HasManual ? "Hand-kept total" : string.Empty;
                if (day.ManualExcluded)
                    detail += $"\nA hand-kept total of {GilFormat.Exact(day.ManualGil)} gil exists for this day and is not counted.";
                if (day.AdjustmentGil != 0)
                    detail += $"\nIncludes a correction of {GilFormat.Exact(day.AdjustmentGil)} gil.";
                bars.Add(new ChartBar(label, month, Math.Max(0, day.IncomeGil), false, title, detail, offset == 0));
            }
            else
            {
                bars.Add(new ChartBar(label, month, 0, true, title, string.Empty, false));
            }
        }

        var size = ImGui.GetContentRegionAvail();
        BarChart.Draw(size, bars, assets, summary.AveragePerDay30, $"30-day average {GilFormat.Short(summary.AveragePerDay30)}");
        Widgets.EndCard();
    }

    private void DrawBreakdowns(UiAssets assets)
    {
        var gap = Theme.S(10f);
        var available = ImGui.GetContentRegionAvail().X;
        var sideBySide = available >= Theme.S(760f);
        var width = sideBySide ? MathF.Floor((available - gap) / 2f) : available;

        var subs = bySubmarine.Take(8).ToList();
        var routes = byRoute.Take(8).ToList();
        var rowHeight = ImGui.GetTextLineHeight() + Theme.S(12f);
        var rows = sideBySide ? Math.Max(subs.Count, routes.Count) : 0;

        float CardHeight(int count) => Theme.S(66f) + rowHeight * (count + 1);

        DrawGroupCard("##bySub", "By submarine", "Submarine", subs, new Vector2(width, CardHeight(sideBySide ? rows : subs.Count)), assets);
        if (sideBySide)
            ImGui.SameLine(0, gap);
        else
            Widgets.VSpace(6f);
        DrawGroupCard("##byRoute", "By route", "Route", routes, new Vector2(width, CardHeight(sideBySide ? rows : routes.Count)), assets);
    }

    private void DrawGroupCard(string id, string title, string firstColumn, List<GroupStats> groups, Vector2 size, UiAssets assets)
    {
        Widgets.BeginCard(id, size);
        Widgets.Text(title, Theme.Text, assets.Heading);
        Widgets.VSpace(2f);

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Theme.S(6f, 6f));
        if (ImGui.BeginTable(id + "table", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn(firstColumn, ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Voyages", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Per voyage", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Per 24h", ImGuiTableColumnFlags.WidthStretch, 1.4f);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Widgets.Text(firstColumn.ToUpperInvariant(), Theme.TextDim, assets.Small);
            foreach (var header in new[] { "VOYAGES", "TOTAL", "PER VOYAGE", "PER 24H" })
            {
                ImGui.TableNextColumn();
                using (assets.Small.Push())
                    Widgets.TextRight(header, Theme.TextDim);
                if (header == "PER 24H")
                    Widgets.Tooltip("Gil earned per 24 hours of real time, measured from one collection to the next. Time a submarine spends waiting at the dock counts against it.");
            }

            foreach (var g in groups)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Widgets.Text(g.Label, Theme.Text);
                ImGui.TableNextColumn();
                Widgets.TextRight(g.Voyages.ToString("N0", CultureInfo.InvariantCulture), Theme.TextSoft);
                ImGui.TableNextColumn();
                Widgets.TextRight(GilFormat.Short(g.TotalGil), Theme.Gold);
                Widgets.Tooltip($"{GilFormat.Exact(g.TotalGil)} gil");
                ImGui.TableNextColumn();
                Widgets.TextRight(GilFormat.Short(g.AveragePerVoyage), Theme.TextSoft);
                Widgets.Tooltip($"{GilFormat.Exact(g.AveragePerVoyage)} gil");
                ImGui.TableNextColumn();
                Widgets.TextRight(g.GilPer24Hours == 0 ? "-" : GilFormat.Short(g.GilPer24Hours), Theme.Teal);
                if (g.GilPer24Hours != 0)
                    Widgets.Tooltip($"{GilFormat.Exact(g.GilPer24Hours)} gil per 24 hours");
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleVar();
        Widgets.EndCard();
    }

    /// <summary>One card per submarine with its last five voyages, so each boat's recent form can be read at a glance.</summary>
    private void DrawLatest(UiAssets assets)
    {
        if (latest.Count == 0)
            return;

        Widgets.Text("Latest voyages", Theme.Text, assets.Heading);
        ImGui.SameLine(0, Theme.S(10f));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Widgets.Measure("A", assets.Heading).Y - ImGui.GetTextLineHeight() - Theme.S(1f));
        Widgets.Text("the last five from each submarine", Theme.TextDim);
        Widgets.VSpace(2f);

        var gap = Theme.S(10f);
        var available = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Clamp((int)((available + gap) / (Theme.S(236f) + gap)), 1, Math.Min(4, latest.Count));
        var width = MathF.Floor((available - gap * (perRow - 1)) / perRow);
        var rowHeight = ImGui.GetTextLineHeight() + Theme.S(12f);
        var showCompany = latest.Any(s => s.FreeCompany.Length > 0);
        var height = Theme.S(showCompany ? 104f : 84f) + rowHeight * 5;
        var calendar = plugin.Calendar;

        for (var i = 0; i < latest.Count; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, gap);

            var sub = latest[i];
            Widgets.BeginCard($"##latest{i}", new Vector2(width, height));

            Widgets.Text(sub.Name, Theme.Text, assets.Heading);
            if (showCompany)
                Widgets.Text(sub.FreeCompany, Theme.TextDim, assets.Small);
            var average = sub.Voyages.Sum(v => v.TotalGil) / sub.Voyages.Count;
            Widgets.Text($"Averaging {GilFormat.Short(average)} over {(sub.Voyages.Count == 1 ? "its last voyage" : $"its last {sub.Voyages.Count}")}", Theme.TextDim, assets.Small);
            Widgets.VSpace(1f);

            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Theme.S(4f, 6f));
            if (ImGui.BeginTable($"##latestTable{i}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 1.5f);
                ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch, 0.7f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1.1f);

                foreach (var v in sub.Voyages)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Widgets.Text(calendar.ToLocal(v.CollectedUnix).ToString("d MMM, HH:mm", CultureInfo.InvariantCulture), Theme.TextSoft);
                    ImGui.TableNextColumn();
                    Widgets.Text(v.Route, Theme.Teal);
                    ImGui.TableNextColumn();
                    Widgets.TextRight(GilFormat.Exact(v.TotalGil), Theme.Gold);
                    if (ImGui.IsItemHovered())
                    {
                        var loot = string.Join("\n", v.Loot.OrderByDescending(l => l.TotalGil).Take(4).Select(l => $"{l.Quantity} x {l.ItemName}: {GilFormat.Exact(l.TotalGil)}"));
                        Widgets.Tooltip($"{GilFormat.Exact(v.TotalGil)} gil\n{loot}{(v.Loot.Count > 4 ? $"\nand {v.Loot.Count - 4} more" : string.Empty)}");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.PopStyleVar();
            Widgets.EndCard();
        }
    }

    private static string Plural(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";
}
