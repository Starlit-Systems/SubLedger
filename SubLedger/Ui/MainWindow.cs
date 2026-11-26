using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using SubLedger.Ui.Pages;

namespace SubLedger.Ui;

internal enum Page
{
    Dashboard,
    Reports,
    Expenses,
    Voyages,
    Console,
    Updates,
    About,
    Settings,
}

/// <summary>The SubLedger window: a branded header, page list down the left, and the current page.</summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly DashboardPage dashboard;
    private readonly ReportsPage reports;
    private readonly ExpensesPage expenses;
    private readonly VoyagesPage voyages;
    private readonly ConsolePage console;
    private readonly UpdatesPage updates;
    private readonly AboutPage about;
    private readonly SettingsPage settings;
    private Page page = Page.Dashboard;

    public MainWindow(Plugin plugin) : base("SubLedger###SubLedgerMain")
    {
        this.plugin = plugin;
        dashboard = new DashboardPage(plugin);
        reports = new ReportsPage(plugin);
        expenses = new ExpensesPage(plugin);
        voyages = new VoyagesPage(plugin);
        console = new ConsolePage(plugin);
        updates = new UpdatesPage(plugin);
        about = new AboutPage(plugin);
        settings = new SettingsPage(plugin);

        Size = new Vector2(1080, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void ShowPage(Page target)
    {
        page = target;
        IsOpen = true;
    }

    public override void PreDraw() => Theme.PushWindow();

    public override void PostDraw() => Theme.PopWindow();

    public override void Draw()
    {
        var assets = plugin.Assets;
        var origin = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail();
        var headerHeight = Theme.S(60f);
        var sidebarWidth = Theme.S(186f);

        DrawHeader(origin, available.X, headerHeight, assets);

        ImGui.SetCursorScreenPos(origin + new Vector2(0, headerHeight));
        var bodyHeight = MathF.Max(1f, available.Y - headerHeight);

        // The window itself has no padding so the header and sidebar reach its edges; the two panels bring their own.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(10f, 12f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Deep);
        if (ImGui.BeginChild("##sidebar", new Vector2(sidebarWidth, bodyHeight), false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar))
            DrawSidebar(assets);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(origin + new Vector2(sidebarWidth, headerHeight), origin + new Vector2(sidebarWidth, available.Y), Theme.U32(Theme.BorderSoft));

        ImGui.SetCursorScreenPos(origin + new Vector2(sidebarWidth + 1f, headerHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(22f, 18f));
        if (ImGui.BeginChild("##content", new Vector2(MathF.Max(1f, available.X - sidebarWidth - 1f), bodyHeight), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            // Popups opened from a page (dropdowns, tooltips) pick up this padding rather than the window's zero.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(10f, 8f));
            try
            {
                switch (page)
                {
                    case Page.Dashboard: dashboard.Draw(); break;
                    case Page.Reports: reports.Draw(); break;
                    case Page.Expenses: expenses.Draw(); break;
                    case Page.Voyages: voyages.Draw(); break;
                    case Page.Console: console.Draw(); break;
                    case Page.Updates: updates.Draw(); break;
                    case Page.About: about.Draw(); break;
                    case Page.Settings: settings.Draw(); break;
                }
            }
            catch (Exception ex)
            {
                // A drawing fault must not take the window (or the game's UI thread) down with it.
                plugin.Log.Error("Window", $"Drawing the {page} page failed", ex);
                page = Page.Console;
            }
            finally
            {
                ImGui.PopStyleVar();
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawHeader(Vector2 origin, float width, float height, UiAssets assets)
    {
        var drawList = ImGui.GetWindowDrawList();
        var end = origin + new Vector2(width, height);

        // Deep water with a faint teal glow behind the emblem, closed by a brass hairline.
        drawList.AddRectFilled(origin, end, Theme.U32(Theme.Deep));
        drawList.AddRectFilledMultiColor(origin, new Vector2(origin.X + MathF.Min(width, Theme.S(520f)), end.Y),
            Theme.U32(Theme.Alpha(Theme.Teal, 0.13f)), Theme.U32(Theme.Alpha(Theme.Teal, 0f)),
            Theme.U32(Theme.Alpha(Theme.Teal, 0f)), Theme.U32(Theme.Alpha(Theme.Teal, 0.05f)));
        drawList.AddRectFilledMultiColor(new Vector2(origin.X, end.Y - 1f), end,
            Theme.U32(Theme.Alpha(Theme.Gold, 0.75f)), Theme.U32(Theme.Alpha(Theme.Gold, 0.04f)),
            Theme.U32(Theme.Alpha(Theme.Gold, 0.04f)), Theme.U32(Theme.Alpha(Theme.Gold, 0.75f)));

        var emblem = Theme.S(42f);
        var emblemPos = origin + new Vector2(Theme.S(14f), (height - emblem) * 0.5f);
        drawList.AddImage(assets.LogoSmall.GetWrapOrEmpty().Handle, emblemPos, emblemPos + new Vector2(emblem, emblem));

        var titleX = emblemPos.X + emblem + Theme.S(12f);
        var titleSize = Widgets.Measure("SubLedger", assets.Heading);
        var subtitle = "Submarine income ledger";
        var subtitleSize = Widgets.Measure(subtitle, assets.Small);
        var blockHeight = titleSize.Y + subtitleSize.Y;
        var titleY = origin.Y + (height - blockHeight) * 0.5f;

        // "Sub" in silver, "Ledger" in gold, as on the logo.
        Widgets.TextAt(drawList, new Vector2(titleX, titleY), "Sub", Theme.Text, assets.Heading);
        var subWidth = Widgets.Measure("Sub", assets.Heading).X;
        Widgets.TextAt(drawList, new Vector2(titleX + subWidth, titleY), "Ledger", Theme.Gold, assets.Heading);
        Widgets.TextAt(drawList, new Vector2(titleX, titleY + titleSize.Y), subtitle, Theme.TextDim, assets.Small);

        // Right side: which Free Company is shown, and whether recording is working.
        var (statusText, statusColor) = plugin.LedgerError is not null
            ? ("LEDGER ERROR", Theme.Rose)
            : plugin.Config.CaptureFromWindow || plugin.Config.CaptureFromPacket
                ? ("RECORDING", Theme.Mint)
                : ("RECORDING OFF", Theme.Amber);
        var pillSize = Widgets.PillSize(statusText, assets);
        var pillPos = new Vector2(end.X - pillSize.X - Theme.S(16f), origin.Y + (height - pillSize.Y) * 0.5f);
        Widgets.PillAt(drawList, pillPos, statusText, statusColor, assets);
        if (ImGui.IsMouseHoveringRect(pillPos, pillPos + pillSize))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(10f, 8f));
            ImGui.BeginTooltip();
            Widgets.Text(plugin.LedgerError is not null
                ? $"The ledger could not be opened, so nothing is being recorded.\n{plugin.LedgerError}\nSee the Console page."
                : plugin.Config.CaptureFromWindow || plugin.Config.CaptureFromPacket
                    ? "Voyage results are recorded automatically as you collect them."
                    : "Both capture triggers are switched off in Settings.", Theme.TextSoft);
            ImGui.EndTooltip();
            ImGui.PopStyleVar();
        }

        DrawFreeCompanyPicker(new Vector2(pillPos.X - Theme.S(14f), origin.Y), height);
    }

    private void DrawFreeCompanyPicker(Vector2 rightEdge, float headerHeight)
    {
        var companies = plugin.Store.Snapshot().FreeCompanies;
        if (companies.Count < 2)
        {
            // With one Free Company there is nothing to choose between.
            if (plugin.Config.SelectedFreeCompany != 0 && companies.All(c => c.FcId != plugin.Config.SelectedFreeCompany))
                plugin.Config.SelectedFreeCompany = 0;
            return;
        }

        var selected = companies.FirstOrDefault(c => c.FcId == plugin.Config.SelectedFreeCompany);
        var preview = selected?.DisplayName ?? "All Free Companies";
        var comboWidth = Theme.S(230f);
        var frameHeight = ImGui.GetFrameHeight();

        ImGui.SetCursorScreenPos(new Vector2(rightEdge.X - comboWidth, rightEdge.Y + (headerHeight - frameHeight) * 0.5f));
        ImGui.SetNextItemWidth(comboWidth);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(8f, 8f));
        if (ImGui.BeginCombo("##fc", preview))
        {
            if (ImGui.Selectable("All Free Companies", plugin.Config.SelectedFreeCompany == 0))
            {
                plugin.Config.SelectedFreeCompany = 0;
                plugin.Config.Save();
            }

            foreach (var fc in companies)
            {
                if (ImGui.Selectable($"{fc.DisplayName}##{fc.FcId:X}", plugin.Config.SelectedFreeCompany == fc.FcId))
                {
                    plugin.Config.SelectedFreeCompany = fc.FcId;
                    plugin.Config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.PopStyleVar();
    }

    private void DrawSidebar(UiAssets assets)
    {
        Tab("Dashboard", FontAwesomeIcon.ChartLine, Page.Dashboard);
        Tab("Reports", FontAwesomeIcon.ChartBar, Page.Reports);
        Tab("Expenses", FontAwesomeIcon.Wallet, Page.Expenses);
        Tab("Voyages", FontAwesomeIcon.Ship, Page.Voyages);
        Tab("Console", FontAwesomeIcon.Terminal, Page.Console, plugin.LedgerError is not null);
        Tab("Updates", FontAwesomeIcon.Newspaper, Page.Updates, plugin.Config.LastSeenChangelog != Changelog.Latest.Version);
        Tab("About", FontAwesomeIcon.InfoCircle, Page.About);
        Tab("Settings", FontAwesomeIcon.SlidersH, Page.Settings);

        // Version, pinned to the bottom of the sidebar.
        var text = $"v{plugin.Version}";
        var size = Widgets.Measure(text, assets.Small);
        var bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() - size.Y - Theme.S(12f);
        if (bottom > ImGui.GetCursorScreenPos().Y)
        {
            Widgets.TextAt(ImGui.GetWindowDrawList(), new Vector2(ImGui.GetWindowPos().X + Theme.S(24f), bottom), text, Theme.TextMuted, assets.Small);
        }
    }

    private void Tab(string label, FontAwesomeIcon icon, Page target, bool attention = false)
    {
        if (Widgets.SidebarTab(label, icon, page == target, attention))
            page = target;
        ImGui.Dummy(new Vector2(0, Theme.S(1f)));
    }
}
