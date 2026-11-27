using System;
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

/// <summary>
/// Records typed or kept by hand: importing a day-by-day list (text or spreadsheet export), and adding corrections.
/// </summary>
internal sealed class ManualRecordsSection
{
    private static readonly string[] DateFormats = ["yyyy-M-d", "M/d/yyyy", "d MMM yyyy"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxFileBytes = 2 * 1024 * 1024;

    private readonly Plugin plugin;

    private string listText = string.Empty;
    private string sourceName = "Pasted list";
    private string startText = string.Empty;
    private string importStatus = string.Empty;
    private bool importFailed;

    private (string Text, string Start, long Version, ulong Fc) planKey;
    private HistoryParseResult? parsed;
    private HistoryImportPlan? plan;
    private bool needsStartDate;

    private string correctionDate = string.Empty;
    private string correctionAmount = string.Empty;
    private string correctionNote = string.Empty;
    private string correctionError = string.Empty;

    public ManualRecordsSection(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private void RefreshPlan()
    {
        var snapshot = plugin.Counted();
        var fc = TargetCompany();
        var key = (listText, startText, snapshot.Version, fc);
        if (key == planKey)
            return;

        planKey = key;
        parsed = null;
        plan = null;
        needsStartDate = false;
        if (string.IsNullOrWhiteSpace(listText))
            return;

        parsed = HistoryParser.Parse(listText);
        var usesDayNumbers = parsed.Rows.Any(r => r.Date is null);
        var haveStart = DateOnly.TryParseExact(startText.Trim(), DateFormats, Inv, DateTimeStyles.None, out var start);
        if (usesDayNumbers && !haveStart)
        {
            needsStartDate = true;
            return;
        }

        var existing = LedgerAnalytics.Daily(snapshot.Voyages, snapshot.Entries, plugin.Calendar, fc);
        plan = HistoryImportPlanner.Plan(parsed.Rows, start, existing);
    }

    private ulong chosenFc;

    /// <summary>The Free Company new records go to: the one picked here, or the one in view, or the only one.</summary>
    private ulong TargetCompany()
    {
        var companies = plugin.Counted().FreeCompanies;
        if (companies.Count < 2)
            return companies.Count == 1 ? companies[0].FcId : 0;
        if (chosenFc == 0 || companies.All(c => c.FcId != chosenFc))
            chosenFc = plugin.ManualEntryFreeCompany() is var inView && inView != 0 ? inView : companies[0].FcId;
        return chosenFc;
    }

    public void Draw(UiAssets assets)
    {
        RefreshPlan();

        var companies = plugin.Counted().FreeCompanies;
        if (companies.Count > 1)
        {
            var target = TargetCompany();
            Widgets.BeginPanel();
            ImGui.AlignTextToFramePadding();
            Widgets.Text("Records entered here belong to", Theme.Text);
            ImGui.SameLine(0, Theme.S(10f));
            ImGui.SetNextItemWidth(Theme.S(280f));
            if (ImGui.BeginCombo("##manualFc", companies.FirstOrDefault(c => c.FcId == target)?.DisplayName ?? "Choose"))
            {
                foreach (var company in companies)
                {
                    if (ImGui.Selectable($"{company.DisplayName}##{company.FcId:X}", company.FcId == target))
                        chosenFc = company.FcId;
                }

                ImGui.EndCombo();
            }

            Widgets.EndPanel();
            Widgets.VSpace(12f);
        }

        DrawImport(assets);
        Widgets.VSpace(12f);
        DrawBatches(assets);
        DrawCorrections(assets);
    }

    private string CompanyNote()
    {
        var fc = TargetCompany();
        var company = plugin.Counted().FreeCompanies.FirstOrDefault(c => c.FcId == fc);
        return company is not null
            ? $"These records will belong to {company.DisplayName}."
            : "No Free Company has been seen yet, so these records will only appear when all Free Companies are shown.";
    }

    private void DrawImport(UiAssets assets)
    {
        Widgets.SectionLabel("Import a day-by-day list", assets);
        Widgets.BeginPanel();
        Widgets.Text(
            "Paste your list below, or load a text or CSV file. One day per line, such as \"Day 1: 125k\", \"Day 2: 1.047m\", "
            + "or a spreadsheet's two columns (day, amount). Lines may also start with a date like 2026-04-23. "
            + "Amounts can be short (125k, 1.047m) or exact (1,047,312).",
            Theme.TextDim);
        Widgets.VSpace(4f);

        if (Widgets.Button("Load a file", FontAwesomeIcon.FolderOpen))
        {
            plugin.FileDialogs.OpenFileDialog("Choose your list", "Lists{.txt,.csv,.tsv},.*", (ok, path) =>
            {
                if (ok)
                    LoadFile(path);
            });
        }

        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Clear", FontAwesomeIcon.Eraser, enabled: listText.Length > 0))
        {
            listText = string.Empty;
            sourceName = "Pasted list";
            importStatus = string.Empty;
        }

        Widgets.VSpace(2f);
        var width = Widgets.PanelRight - Widgets.PanelLeft;
        if (ImGui.InputTextMultiline("##historyList", ref listText, 400_000, new Vector2(width, Theme.S(150f))))
        {
            importStatus = string.Empty;
            if (sourceName != "Pasted list" && string.IsNullOrWhiteSpace(listText))
                sourceName = "Pasted list";
        }

        Widgets.VSpace(2f);
        ImGui.AlignTextToFramePadding();
        Widgets.Text("Day 1 was", Theme.Text);
        ImGui.SameLine(0, Theme.S(8f));
        ImGui.SetNextItemWidth(Theme.S(130f));
        ImGui.InputTextWithHint("##historyStart", "yyyy-mm-dd", ref startText, 16);
        ImGui.SameLine(0, Theme.S(10f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text("Only needed for lines numbered \"Day 1\", \"Day 2\" and so on.", Theme.TextDim);

        if (parsed is not null)
        {
            Widgets.PanelDivider();
            DrawPreview(assets);
        }

        if (!string.IsNullOrEmpty(importStatus))
        {
            Widgets.VSpace(2f);
            Widgets.Text(importStatus, importFailed ? Theme.Rose : Theme.Mint);
        }

        Widgets.EndPanel();
    }

    private void DrawPreview(UiAssets assets)
    {
        if (parsed is null)
            return;

        if (parsed.Rows.Count == 0)
        {
            Widgets.Text("None of these lines could be read as a day and an amount.", Theme.Amber);
        }
        else if (needsStartDate)
        {
            Widgets.Text($"{parsed.Rows.Count} lines read. Enter the date of Day 1 above to see what the import will do.", Theme.Amber);
        }

        foreach (var problem in parsed.Problems.Take(4))
            Widgets.Text(problem, Theme.Amber);
        if (parsed.Problems.Count > 4)
            Widgets.Text($"...and {parsed.Problems.Count - 4} more lines that could not be read. They will be left out.", Theme.Amber);

        if (plan is null || plan.Rows.Count == 0)
            return;

        Widgets.Text($"{plan.CountedDays} {(plan.CountedDays == 1 ? "day" : "days")} will be added and counted: {GilFormat.Exact(plan.CountedGil)} gil.", Theme.Text);
        if (plan.OverlapDays > 0)
            Widgets.Text($"{plan.OverlapDays} {(plan.OverlapDays == 1 ? "day already has" : "days already have")} recorded voyages. Those totals ({GilFormat.Exact(plan.OverlapGil)} gil) are kept on file and marked, but the recorded voyages are what count, so nothing is doubled.", Theme.TextSoft);
        if (plan.SkippedCount > 0)
            Widgets.Text($"{plan.SkippedCount} {(plan.SkippedCount == 1 ? "line is" : "lines are")} skipped: the day is already in the ledger from an earlier import, or is repeated further down the list.", Theme.TextSoft);
        Widgets.Text(CompanyNote(), Theme.TextDim);
        Widgets.VSpace(2f);

        var rowHeight = ImGui.GetTextLineHeight() + Theme.S(10f);
        var visible = Math.Min(plan.Rows.Count, 7);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Mix(Theme.Abyss, Theme.Deep, 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.S(6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(10f, 6f));
        if (ImGui.BeginChild("##historyPreview", new Vector2(Widgets.PanelRight - Widgets.PanelLeft, rowHeight * (visible + 1) + Theme.S(14f)), true))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Theme.S(6f, 5f));
            if (ImGui.BeginTable("##historyTable", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Line", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                ImGui.TableSetupColumn("Day", ImGuiTableColumnFlags.WidthStretch, 1.6f);
                ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                ImGui.TableSetupColumn("What happens", ImGuiTableColumnFlags.WidthStretch, 3f);

                ImGui.TableNextRow();
                foreach (var header in new[] { "LINE", "DAY", "AMOUNT (GIL)", "WHAT HAPPENS" })
                {
                    ImGui.TableNextColumn();
                    Widgets.Text(header, Theme.TextDim, assets.Small);
                }

                foreach (var row in plan.Rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Widgets.Text(row.Row.LineNumber.ToString(Inv), Theme.TextDim);
                    ImGui.TableNextColumn();
                    Widgets.Text(row.Date.ToString("ddd d MMM yyyy", Inv), Theme.TextSoft);
                    ImGui.TableNextColumn();
                    Widgets.Text(GilFormat.Exact(row.Row.Gil), row.Status == HistoryRowStatus.Counts ? Theme.Gold : Theme.TextDim);
                    ImGui.TableNextColumn();
                    var (text, color) = row.Status switch
                    {
                        HistoryRowStatus.Counts => ("Added and counted", Theme.Mint),
                        HistoryRowStatus.KeptButNotCounted => ("Kept on file, not counted: voyages are recorded for this day", Theme.Amber),
                        HistoryRowStatus.AlreadyImported => ("Skipped: this day already has a hand-kept total", Theme.TextDim),
                        _ => ("Skipped: this day appears again further down", Theme.TextDim),
                    };
                    Widgets.Text(text, color);
                }

                ImGui.EndTable();
            }

            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        Widgets.VSpace(4f);
        var count = plan.ImportCount;
        if (Widgets.Button(count == 0 ? "Nothing to import" : $"Import {count} {(count == 1 ? "day" : "days")}", FontAwesomeIcon.FileImport, Widgets.ButtonKind.Primary, count > 0))
            RunImport();
    }

    private void RunImport()
    {
        if (plan is null || plan.ImportCount == 0)
            return;

        var batchId = Guid.NewGuid().ToString("N");
        var source = sourceName;
        var entries = HistoryImportPlanner.ToEntries(plan, plugin.Store, TargetCompany(), source, batchId);
        var counted = plan.CountedDays;
        var gil = plan.CountedGil;
        var overlap = plan.OverlapDays;

        plugin.Worker.Enqueue("Import hand-kept list", () =>
        {
            plugin.Store.WithExclusiveAccess(() => plugin.Backups.Create("before-import"));
            plugin.Store.AddEntries(entries);
            plugin.Log.Info("Import", $"Hand-kept list \"{source}\": {entries.Count} days imported, {counted} counted ({GilFormat.Exact(gil)} gil), {overlap} kept but not counted.");
        });

        importFailed = false;
        importStatus = $"Imported {entries.Count} {(entries.Count == 1 ? "day" : "days")} from \"{source}\". {GilFormat.Exact(gil)} gil now counts towards your totals.";
        listText = string.Empty;
        sourceName = "Pasted list";
    }

    private void LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                importFailed = true;
                importStatus = "That file is larger than 2 MB, which is far more than a list of daily totals should be. Nothing was loaded.";
                return;
            }

            listText = File.ReadAllText(path);
            sourceName = info.Name;
            importFailed = false;
            importStatus = string.Empty;
        }
        catch (Exception ex)
        {
            importFailed = true;
            importStatus = $"Could not read that file: {ex.Message}";
            plugin.Log.Warning("Import", $"Could not read {path}: {ex.Message}");
        }
    }

    private void DrawBatches(UiAssets assets)
    {
        var batches = plugin.Counted().Entries
            .Where(e => e.Kind == LedgerEntryKind.HistoricalDay && e.BatchId.Length > 0)
            .GroupBy(e => e.BatchId)
            .Select(g => (Id: g.Key, Source: g.First().Source, Count: g.Count(), Gil: g.Sum(e => e.AmountGil),
                Imported: g.Min(e => e.CreatedUnix), From: g.Min(e => e.BusinessDate), To: g.Max(e => e.BusinessDate)))
            .OrderByDescending(b => b.Imported)
            .ToList();
        if (batches.Count == 0)
            return;

        Widgets.SectionLabel("Lists already imported", assets);
        Widgets.BeginPanel();
        for (var i = 0; i < batches.Count; i++)
        {
            if (i > 0)
                Widgets.PanelDivider();

            var batch = batches[i];
            var start = ImGui.GetCursorScreenPos();
            ImGui.BeginGroup();
            // Wrap short of the Remove button so long descriptions never run underneath it.
            ImGui.PushTextWrapPos(Widgets.PanelRight - Theme.S(120f) - ImGui.GetWindowPos().X + ImGui.GetScrollX());
            Widgets.Text(batch.Source, Theme.Text);
            Widgets.Text(
                $"{batch.Count} {(batch.Count == 1 ? "day" : "days")}, {batch.From.ToString("d MMM yyyy", Inv)} to {batch.To.ToString("d MMM yyyy", Inv)} · {GilFormat.Exact(batch.Gil)} gil · imported {plugin.Calendar.ToLocal(batch.Imported).ToString("d MMM yyyy", Inv)}",
                Theme.TextDim);
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var height = ImGui.GetItemRectSize().Y;

            ImGui.SetCursorScreenPos(new Vector2(Widgets.PanelRight - Theme.S(104f), start.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
            ImGui.PushID(batch.Id);
            if (Widgets.Button("Remove", FontAwesomeIcon.Trash, Widgets.ButtonKind.Danger) && ImGui.GetIO().KeyCtrl)
            {
                var id = batch.Id;
                var source = batch.Source;
                plugin.Worker.Enqueue("Remove imported list", () =>
                {
                    var removed = plugin.Store.DeleteBatch(id);
                    plugin.Log.Info("Import", $"Removed imported list \"{source}\": {removed} days.");
                });
            }

            Widgets.Tooltip("Hold Ctrl and click. Every day that came from this list leaves the ledger. Recorded voyages are not touched.");
            ImGui.PopID();
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + MathF.Max(height, ImGui.GetFrameHeight())));
            ImGui.Dummy(new Vector2(0f, Theme.S(2f)));
        }

        Widgets.EndPanel();
        Widgets.VSpace(12f);
    }

    private void DrawCorrections(UiAssets assets)
    {
        Widgets.SectionLabel("Corrections and additions", assets);
        Widgets.BeginPanel();
        Widgets.Text(
            "Adjust a day by hand: a positive amount adds income the ledger did not see, a negative one takes some away. "
            + "Corrections always count, on top of whatever else the day holds.",
            Theme.TextDim);
        Widgets.VSpace(4f);

        ImGui.SetNextItemWidth(Theme.S(130f));
        ImGui.InputTextWithHint("##correctionDate", "yyyy-mm-dd", ref correctionDate, 16);
        ImGui.SameLine(0, Theme.S(8f));
        ImGui.SetNextItemWidth(Theme.S(150f));
        ImGui.InputTextWithHint("##correctionAmount", "Amount, e.g. -12k", ref correctionAmount, 24);
        ImGui.SameLine(0, Theme.S(8f));
        ImGui.SetNextItemWidth(MathF.Max(Theme.S(120f), Widgets.PanelRight - ImGui.GetCursorScreenPos().X - Theme.S(170f)));
        ImGui.InputTextWithHint("##correctionNote", "What is this for? (optional)", ref correctionNote, 120);
        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Add correction", FontAwesomeIcon.Plus, Widgets.ButtonKind.Primary))
            AddCorrection();

        if (!string.IsNullOrEmpty(correctionError))
            Widgets.Text(correctionError, Theme.Rose);

        var corrections = plugin.Counted().Entries
            .Where(e => e.Kind == LedgerEntryKind.Adjustment)
            .OrderByDescending(e => e.BusinessDate).ThenByDescending(e => e.CreatedUnix)
            .ToList();
        foreach (var entry in corrections.Take(30))
        {
            Widgets.PanelDivider();
            var start = ImGui.GetCursorScreenPos();
            ImGui.AlignTextToFramePadding();
            Widgets.Text(entry.BusinessDate.ToString("ddd d MMM yyyy", Inv), Theme.TextSoft);
            ImGui.SameLine(Theme.S(170f));
            ImGui.AlignTextToFramePadding();
            Widgets.Text((entry.AmountGil > 0 ? "+" : string.Empty) + GilFormat.Exact(entry.AmountGil), entry.AmountGil < 0 ? Theme.Rose : Theme.Gold);
            ImGui.SameLine(Theme.S(320f));
            ImGui.AlignTextToFramePadding();
            Widgets.Text(string.IsNullOrWhiteSpace(entry.Note) ? "No note" : entry.Note, Theme.TextDim);

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(Widgets.PanelRight - Theme.S(104f), start.Y));
            ImGui.PushID(entry.Id);
            if (Widgets.Button("Remove", FontAwesomeIcon.Trash, Widgets.ButtonKind.Danger) && ImGui.GetIO().KeyCtrl)
            {
                var id = entry.Id;
                plugin.Worker.Enqueue("Remove correction", () =>
                {
                    if (plugin.Store.SetEntryDeleted(id, true))
                        plugin.Log.Info("Ledger", "Correction removed.");
                });
            }

            Widgets.Tooltip("Hold Ctrl and click to remove this correction.");
            ImGui.PopID();
        }

        if (corrections.Count > 30)
            Widgets.Text($"...and {corrections.Count - 30} older corrections.", Theme.TextDim);

        Widgets.EndPanel();
    }

    private void AddCorrection()
    {
        if (!DateOnly.TryParseExact(correctionDate.Trim(), DateFormats, Inv, DateTimeStyles.None, out var date))
        {
            correctionError = "Enter the day as year-month-day, for example 2026-11-26.";
            return;
        }

        if (!HistoryParser.TryParseAmount(correctionAmount, out var gil) || gil == 0)
        {
            correctionError = "Enter an amount such as 3,500 or -12k.";
            return;
        }

        correctionError = string.Empty;
        var entry = plugin.Store.NewEntry(LedgerEntryKind.Adjustment, date, gil, TargetCompany(), correctionNote.Trim(), "Entered by hand");
        plugin.Worker.Enqueue("Add correction", () =>
        {
            plugin.Store.AddEntries([entry]);
            plugin.Log.Info("Ledger", $"Correction added for {date.ToString("d MMM yyyy", Inv)}: {GilFormat.Exact(gil)} gil.");
        });

        correctionAmount = string.Empty;
        correctionNote = string.Empty;
    }
}
