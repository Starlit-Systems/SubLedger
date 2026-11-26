using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Import;
using SubLedger.Core.Model;

namespace SubLedger.Ui.Pages;

/// <summary>What the fleet cost, what it costs to run, and whether it has paid for itself.</summary>
internal sealed class ExpensesPage
{
    private static readonly string[] DateFormats = ["yyyy-M-d", "M/d/yyyy", "d MMM yyyy"];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Plugin plugin;

    private (long Version, ulong Fc, string Zone, int Cutoff, DateOnly Today) cacheKey;
    private FinanceSummary finance = new();
    private List<LedgerEntry> costs = [];

    // The form.
    private LedgerEntryKind formKind = LedgerEntryKind.Investment;
    private string formCategory = "Submarine parts";
    private string formDescription = string.Empty;
    private int formQuantity = 1;
    private string formPrice = string.Empty;
    private string formDate = string.Empty;
    private ulong formFc;
    private string formError = string.Empty;
    private string formStatus = string.Empty;

    public ExpensesPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private void Refresh()
    {
        var calendar = plugin.Calendar;
        var today = calendar.Today(DateTimeOffset.Now);
        var snapshot = plugin.Store.Snapshot();
        var fc = plugin.Config.SelectedFreeCompany;
        var key = (snapshot.Version, fc, calendar.Zone.Id, calendar.CutoffMinutes, today);
        if (key == cacheKey)
            return;

        cacheKey = key;
        var days = LedgerAnalytics.Daily(snapshot.Voyages, snapshot.Entries, calendar, fc);
        finance = FinanceAnalytics.Summarize(days, snapshot.Entries, today, fc);
        costs = [.. snapshot.Entries
            .Where(e => ExpenseCategories.IsCost(e.Kind) && (fc == 0 || e.FcId == fc))
            .OrderByDescending(e => e.BusinessDate).ThenByDescending(e => e.CreatedUnix)];
    }

    public void Draw()
    {
        Refresh();
        var assets = plugin.Assets;
        Widgets.PageHeader("Expenses & payback", "What the fleet cost to build and run, and when it paid for itself.", assets);

        if (finance.HasSpending)
        {
            DrawPayback(assets);
            Widgets.VSpace(6f);
            DrawTiles(assets);
            Widgets.VSpace(6f);
            if (finance.Timeline.Count >= 2)
            {
                DrawChart(assets);
                Widgets.VSpace(10f);
            }
        }
        else
        {
            Widgets.BeginPanel();
            Widgets.Text("Add what you spent to see when your fleet paid for itself", Theme.Text, assets.Heading);
            Widgets.Text(
                "Enter one-off purchases, such as submarine parts, your Free Company house and the plot, as investments, and ongoing "
                + "costs such as ceruleum tanks and repair materials as running costs. SubLedger then works out your profit, your "
                + "return on investment, and the day your submarines earned it all back. Rough figures are fine; you can change them later.",
                Theme.TextSoft);
            Widgets.EndPanel();
            Widgets.VSpace(10f);
        }

        DrawForm(assets);
        Widgets.VSpace(10f);

        if (finance.ByCategory.Count > 0)
        {
            DrawCategories(assets);
            Widgets.VSpace(10f);
        }

        DrawList(assets);
        Widgets.VSpace(8f);
    }

    private void DrawPayback(UiAssets assets)
    {
        Widgets.BeginPanel();
        var share = finance.RecoveredShare;
        string headline;
        string detail;
        Vector4 accent;

        if (share is null)
        {
            headline = "No investment entered yet";
            detail = $"Running costs so far: {GilFormat.Exact(finance.RunningCostsGil)} gil. Add what the parts, house and plot cost to see when they were paid back.";
            accent = Theme.Teal;
        }
        else if (finance.PaidBackOn is { } paidOn)
        {
            headline = $"Paid back on {paidOn.ToString("d MMMM yyyy", Inv)}";
            detail = $"Your submarines have earned back {Percent(share.Value)} of the {GilFormat.Exact(finance.InvestedGil)} gil invested. Everything since is profit.";
            accent = Theme.Mint;
        }
        else
        {
            headline = $"{Percent(share.Value)} paid back";
            detail = finance.ProjectedPaybackOn is { } projected
                ? $"{GilFormat.Exact(-finance.ProfitGil)} gil still to earn back. At the last 30 days' pace that is around {projected.ToString("d MMMM yyyy", Inv)}."
                : $"{GilFormat.Exact(-finance.ProfitGil)} gil still to earn back.";
            accent = Theme.Amber;
        }

        Widgets.Text(headline, accent, assets.Title);
        Widgets.Text(detail, Theme.TextSoft);

        if (share is { } s)
        {
            Widgets.VSpace(6f);
            DrawMeter(s, accent, assets);
        }

        Widgets.EndPanel(accent: accent);
    }

    /// <summary>
    /// How much of the investment has been earned back. The track is the investment; past 100% the bar keeps a full
    /// track and a marker shows where "paid back" fell.
    /// </summary>
    private static void DrawMeter(double share, Vector4 color, UiAssets assets)
    {
        var drawList = ImGui.GetWindowDrawList();
        var left = Widgets.PanelLeft;
        var width = Widgets.PanelRight - left;
        var top = ImGui.GetCursorScreenPos().Y;
        var height = Theme.S(10f);
        var scale = Math.Max(1.0, share);

        drawList.AddRectFilled(new Vector2(left, top), new Vector2(left + width, top + height), Theme.U32(Theme.Mix(Theme.Card, color, 0.16f)), height * 0.5f);
        var filled = (float)(width * Math.Clamp(share, 0, scale) / scale);
        if (filled > 0)
            drawList.AddRectFilled(new Vector2(left, top), new Vector2(left + filled, top + height), Theme.U32(color), height * 0.5f);

        if (share > 1)
        {
            // Where the investment was covered.
            var x = left + (float)(width / scale);
            drawList.AddLine(new Vector2(x, top - Theme.S(4f)), new Vector2(x, top + height + Theme.S(4f)), Theme.U32(Theme.Text), Theme.S(2f));
        }

        ImGui.Dummy(new Vector2(width, height + Theme.S(4f)));
        var label = share > 1 ? "The white mark is where the investment was covered." : "The full bar is the amount invested.";
        Widgets.Text(label, Theme.TextDim, assets.Small);
    }

    private void DrawTiles(UiAssets assets)
    {
        var gap = Theme.S(10f);
        var available = ImGui.GetContentRegionAvail().X;
        var perRow = available >= Theme.S(720f) ? 4 : 2;
        var width = MathF.Floor((available - gap * (perRow - 1)) / perRow);
        var size = new Vector2(width, Theme.S(104f));
        var share = finance.RecoveredShare;

        var tiles = new (string Label, string Value, string? Detail, Vector4 Accent, string Tip)[]
        {
            ("Invested", Short(finance.InvestedGil), $"{finance.ByCategory.Where(c => c.Kind == LedgerEntryKind.Investment).Sum(c => c.Entries)} purchases", Theme.Violet,
                $"{GilFormat.Exact(finance.InvestedGil)} gil in one-off purchases."),
            ("Earned", Short(finance.IncomeGil), finance.RunningCostsGil == 0 ? "No running costs entered" : $"{GilFormat.Short(finance.RunningCostsGil)} running costs", Theme.Gold,
                $"{GilFormat.Exact(finance.IncomeGil)} gil earned by the submarines.\n{GilFormat.Exact(finance.RunningCostsGil)} gil spent keeping them running.\n{GilFormat.Exact(finance.NetIncomeGil)} gil after running costs."),
            ("Profit", Signed(finance.ProfitGil), finance.ProfitGil >= 0 ? "After paying for everything" : "Still to earn back", finance.ProfitGil >= 0 ? Theme.Mint : Theme.Amber,
                $"Earnings after running costs, minus the investment: {GilFormat.Exact(finance.ProfitGil)} gil."),
            ("Return", share is { } s ? Percent(s) : "-", $"{GilFormat.Short(finance.NetPerDay30)} a day lately", Theme.Teal,
                "Earnings after running costs as a share of what was invested. 100% means exactly paid back.\n"
                + $"Over the last 30 days the fleet earned {GilFormat.Exact(finance.NetPerDay30)} gil a day after running costs."),
        };

        for (var i = 0; i < tiles.Length; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine(0, gap);
            var t = tiles[i];
            Widgets.StatTile(t.Label, t.Value, t.Detail, t.Accent, size, assets, t.Tip);
        }
    }

    private string Short(long gil) => GilFormat.Format(gil, plugin.Config.AbbreviateGil);

    private string Signed(long gil) => gil > 0 ? "+" + Short(gil) : Short(gil);

    private static string Percent(double share)
        => (share * 100).ToString(share >= 10 ? "N0" : "0.#", Inv) + "%";

    private void DrawChart(UiAssets assets)
    {
        Widgets.BeginCard("##paybackChart", new Vector2(0, Theme.S(300f)));
        Widgets.Text("Earned against invested", Theme.Text, assets.Heading);
        Widgets.Text("Where the gold line climbs above the teal one, the fleet has paid for itself.", Theme.TextDim);
        Widgets.VSpace(2f);

        var timeline = finance.Timeline;
        var points = new List<ChartBar>(timeline.Count);
        foreach (var p in timeline)
        {
            var gap = p.CumulativeNetIncome - p.CumulativeInvested;
            var detail = p.CumulativeInvested == 0 ? string.Empty
                : gap >= 0 ? $"{GilFormat.Exact(gap)} gil ahead" : $"{GilFormat.Exact(-gap)} gil still to earn back";
            points.Add(new ChartBar(p.Date.Day.ToString(Inv), p.Date.ToString("MMM", Inv), 0, false, p.Date.ToString("dddd, d MMMM yyyy", Inv), detail, false));
        }

        // Long timelines label months rather than days.
        if (timeline.Count > 120)
        {
            for (var i = 0; i < points.Count; i++)
                points[i] = points[i] with { AxisLabel = string.Empty, AxisGroup = timeline[i].Date.ToString("MMM yy", Inv) };
        }

        BarChart.DrawLines(ImGui.GetContentRegionAvail(), points,
        [
            new LineSeries("Earned after running costs", Theme.SeriesGold, [.. timeline.Select(p => Math.Max(0, p.CumulativeNetIncome))], Wash: true),
            new LineSeries("Invested", Theme.SeriesTeal, [.. timeline.Select(p => p.CumulativeInvested)], Wash: false),
        ], assets);
        Widgets.EndCard();
    }

    private void DrawForm(UiAssets assets)
    {
        var calendar = plugin.Calendar;
        if (formDate.Length == 0)
            formDate = calendar.Today(DateTimeOffset.Now).ToString("yyyy-MM-dd", Inv);

        Widgets.SectionLabel("Add spending", assets);
        Widgets.BeginPanel();

        if (Widgets.FilterPill("Investment", Theme.Violet, formKind == LedgerEntryKind.Investment, assets, "kindInvestment"))
            SetKind(LedgerEntryKind.Investment);
        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.FilterPill("Running cost", Theme.Teal, formKind == LedgerEntryKind.Expense, assets, "kindExpense"))
            SetKind(LedgerEntryKind.Expense);
        ImGui.SameLine(0, Theme.S(12f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text(formKind == LedgerEntryKind.Investment
            ? "A one-off purchase the fleet has to earn back."
            : "An ongoing cost of keeping the submarines out.", Theme.TextDim);
        Widgets.VSpace(4f);

        var labelWidth = Theme.S(104f);
        void Label(string text)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.Text(text, Theme.TextSoft);
            ImGui.SameLine(Widgets.PanelLeft - ImGui.GetWindowPos().X + labelWidth);
        }

        Label("Category");
        ImGui.SetNextItemWidth(Theme.S(220f));
        if (ImGui.BeginCombo("##expenseCategory", formCategory))
        {
            foreach (var name in ExpenseCategories.For(formKind))
            {
                if (ImGui.Selectable(name, formCategory == name))
                    formCategory = name;
            }

            ImGui.EndCombo();
        }

        Label("What was it");
        ImGui.SetNextItemWidth(MathF.Min(Theme.S(420f), Widgets.PanelRight - ImGui.GetCursorScreenPos().X));
        ImGui.InputTextWithHint("##expenseDescription", formKind == LedgerEntryKind.Investment ? "e.g. Shark-class Bow" : "e.g. 99 ceruleum tanks", ref formDescription, 120);

        Label("Quantity");
        ImGui.SetNextItemWidth(Theme.S(110f));
        if (ImGui.InputInt("##expenseQuantity", ref formQuantity, 1))
            formQuantity = Math.Clamp(formQuantity, 1, 9999);
        ImGui.SameLine(0, Theme.S(16f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text("Price each", Theme.TextSoft);
        ImGui.SameLine(0, Theme.S(8f));
        ImGui.SetNextItemWidth(Theme.S(150f));
        ImGui.InputTextWithHint("##expensePrice", "e.g. 4m", ref formPrice, 24);

        var priceOk = HistoryParser.TryParseAmount(formPrice, out var price) && price > 0;
        if (priceOk)
        {
            ImGui.SameLine(0, Theme.S(12f));
            ImGui.AlignTextToFramePadding();
            Widgets.Text($"= {GilFormat.Exact(price * formQuantity)} gil", Theme.Gold);
        }

        Label("Date");
        ImGui.SetNextItemWidth(Theme.S(130f));
        ImGui.InputTextWithHint("##expenseDate", "yyyy-mm-dd", ref formDate, 16);
        ImGui.SameLine(0, Theme.S(10f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text("When it was bought. A rough date is fine.", Theme.TextDim);

        var companies = plugin.Store.Snapshot().FreeCompanies;
        if (companies.Count > 1)
        {
            if (formFc == 0 || companies.All(c => c.FcId != formFc))
                formFc = plugin.ManualEntryFreeCompany() is var chosen && chosen != 0 ? chosen : companies[0].FcId;

            Label("Free Company");
            ImGui.SetNextItemWidth(Theme.S(260f));
            if (ImGui.BeginCombo("##expenseFc", companies.FirstOrDefault(c => c.FcId == formFc)?.DisplayName ?? "Choose"))
            {
                foreach (var company in companies)
                {
                    if (ImGui.Selectable($"{company.DisplayName}##{company.FcId:X}", formFc == company.FcId))
                        formFc = company.FcId;
                }

                ImGui.EndCombo();
            }
        }

        Widgets.VSpace(4f);
        if (Widgets.Button("Add", FontAwesomeIcon.Plus, Widgets.ButtonKind.Primary))
            Add(companies.Count > 1 ? formFc : plugin.ManualEntryFreeCompany());

        if (!string.IsNullOrEmpty(formError))
        {
            ImGui.SameLine(0, Theme.S(12f));
            ImGui.AlignTextToFramePadding();
            Widgets.Text(formError, Theme.Rose);
        }
        else if (!string.IsNullOrEmpty(formStatus))
        {
            ImGui.SameLine(0, Theme.S(12f));
            ImGui.AlignTextToFramePadding();
            Widgets.Text(formStatus, Theme.Mint);
        }

        Widgets.EndPanel();
    }

    private void SetKind(LedgerEntryKind kind)
    {
        if (formKind == kind)
            return;

        formKind = kind;
        formCategory = ExpenseCategories.For(kind).First();
    }

    private void Add(ulong fcId)
    {
        formStatus = string.Empty;
        if (!HistoryParser.TryParseAmount(formPrice, out var price) || price <= 0)
        {
            formError = "Enter a price such as 4m or 350,000.";
            return;
        }

        if (!DateOnly.TryParseExact(formDate.Trim(), DateFormats, Inv, DateTimeStyles.None, out var date))
        {
            formError = "Enter the date as year-month-day, for example 2026-04-20.";
            return;
        }

        formError = string.Empty;
        var total = price * formQuantity;
        var description = formDescription.Trim();
        var note = formQuantity > 1
            ? $"{formQuantity} x {(description.Length > 0 ? description : formCategory)} at {GilFormat.Exact(price)} each"
            : description;

        var entry = plugin.Store.NewEntry(formKind, date, total, fcId, note, "Entered by hand", category: formCategory);
        var label = formKind == LedgerEntryKind.Investment ? "Investment" : "Running cost";
        var category = formCategory;
        plugin.Worker.Enqueue("Add spending", () =>
        {
            plugin.Store.AddEntries([entry]);
            plugin.Log.Info("Ledger", $"{label} added: {category}, {GilFormat.Exact(total)} gil on {date.ToString("d MMM yyyy", Inv)}.");
        });

        formStatus = $"Added {GilFormat.Exact(total)} gil.";
        formDescription = string.Empty;
        formPrice = string.Empty;
        formQuantity = 1;
    }

    private void DrawCategories(UiAssets assets)
    {
        Widgets.BeginPanel();
        Widgets.Text("Where the money went", Theme.Text, assets.Heading);
        Widgets.VSpace(4f);
        var rows = finance.ByCategory
            .Select(c => new RankedItem(c.Category, c.TotalGil,
                $"{(c.Kind == LedgerEntryKind.Investment ? "Investment" : "Running cost")} · {c.Entries} {(c.Entries == 1 ? "entry" : "entries")}"))
            .ToList();
        BarChart.DrawRanked(rows, Math.Max(1, finance.InvestedGil + finance.RunningCostsGil), assets);
        Widgets.EndPanel();
    }

    private void DrawList(UiAssets assets)
    {
        if (costs.Count == 0)
            return;

        Widgets.SectionLabel("Everything entered", assets);
        Widgets.BeginPanel();
        var companies = plugin.Store.Snapshot().FreeCompanies;
        var showCompany = companies.Count > 1 && plugin.Config.SelectedFreeCompany == 0;

        for (var i = 0; i < costs.Count; i++)
        {
            if (i > 0)
                Widgets.PanelDivider();

            var entry = costs[i];
            var start = ImGui.GetCursorScreenPos();
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(Widgets.PanelRight - Theme.S(260f) - ImGui.GetWindowPos().X + ImGui.GetScrollX());
            var title = string.IsNullOrWhiteSpace(entry.Note) ? entry.Category : $"{entry.Category}: {entry.Note}";
            Widgets.Text(title, Theme.Text);
            var meta = $"{entry.BusinessDate.ToString("d MMM yyyy", Inv)} · {(entry.Kind == LedgerEntryKind.Investment ? "investment" : "running cost")}";
            if (showCompany)
                meta += " · " + (companies.FirstOrDefault(c => c.FcId == entry.FcId)?.DisplayName ?? "no Free Company");
            Widgets.Text(meta, Theme.TextDim);
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var height = MathF.Max(ImGui.GetItemRectSize().Y, ImGui.GetFrameHeight());

            var amount = GilFormat.Exact(entry.AmountGil);
            var amountWidth = ImGui.CalcTextSize(amount).X;
            var buttonX = Widgets.PanelRight - Theme.S(104f);
            ImGui.SetCursorScreenPos(new Vector2(buttonX - amountWidth - Theme.S(16f), start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
            Widgets.Text(amount, entry.Kind == LedgerEntryKind.Investment ? Theme.Violet : Theme.Teal);

            ImGui.SetCursorScreenPos(new Vector2(buttonX, start.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
            ImGui.PushID(entry.Id);
            if (Widgets.Button("Remove", FontAwesomeIcon.Trash, Widgets.ButtonKind.Danger) && ImGui.GetIO().KeyCtrl)
            {
                var id = entry.Id;
                var what = title;
                plugin.Worker.Enqueue("Remove spending", () =>
                {
                    if (plugin.Store.SetEntryDeleted(id, true))
                        plugin.Log.Info("Ledger", $"Spending removed: {what}.");
                });
            }

            Widgets.Tooltip("Hold Ctrl and click to remove this entry.");
            ImGui.PopID();
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
            ImGui.Dummy(new Vector2(0f, Theme.S(2f)));
        }

        Widgets.EndPanel();
    }
}
