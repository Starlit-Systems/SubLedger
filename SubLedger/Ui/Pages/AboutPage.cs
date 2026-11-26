using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Utility;

namespace SubLedger.Ui.Pages;

/// <summary>Who made this, how, and what it stands on.</summary>
internal sealed class AboutPage
{
    private readonly Plugin plugin;

    public AboutPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var assets = plugin.Assets;
        var available = ImGui.GetContentRegionAvail().X;
        var column = MathF.Min(available, Theme.S(680f));
        var left = ImGui.GetCursorScreenPos().X + (available - column) * 0.5f;

        // The logo, with a slow glow behind it.
        var logoSize = MathF.Min(Theme.S(250f), column * 0.6f);
        var logoPos = new Vector2(left + (column - logoSize) * 0.5f, ImGui.GetCursorScreenPos().Y + Theme.S(4f));
        var drawList = ImGui.GetWindowDrawList();
        var center = logoPos + new Vector2(logoSize, logoSize) * 0.5f;
        var glow = 0.05f + 0.04f * Theme.Pulse(4200);
        for (var ring = 5; ring >= 1; ring--)
            drawList.AddCircleFilled(center, logoSize * (0.42f + ring * 0.045f), Theme.U32(Theme.Alpha(Theme.Teal, glow / ring)), 64);
        drawList.AddImage(assets.Logo.GetWrapOrEmpty().Handle, logoPos, logoPos + new Vector2(logoSize, logoSize));
        ImGui.Dummy(new Vector2(0, logoSize + Theme.S(10f)));

        Centered("An accounting ledger for your Free Company submarines.", Theme.TextSoft, left, column);
        Widgets.VSpace(4f);
        var versionText = $"VERSION {plugin.Version}";
        var pill = Widgets.PillSize(versionText, assets, dot: false);
        ImGui.SetCursorScreenPos(new Vector2(left + (column - pill.X) * 0.5f, ImGui.GetCursorScreenPos().Y));
        Widgets.Pill(versionText, Theme.Gold, assets, dot: false);
        Widgets.VSpace(14f);

        // Made by.
        ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
        Widgets.BeginPanel(column);
        Widgets.Text("Made by Starlit Systems", Theme.Gold, assets.Heading);
        Widgets.Text(
            "SubLedger only watches. It reads the voyage results the game already shows you and writes them to a ledger on your own computer. "
            + "It never clicks, never sends anything to the game, and never sends your data anywhere.",
            Theme.TextSoft);
        Widgets.EndPanel(accent: Theme.Gold);
        Widgets.VSpace(10f);

        // Vibe coded.
        ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
        Widgets.BeginPanel(column);
        Widgets.Text("Vibe coded", Theme.Rose, assets.Heading);
        Widgets.Text("This plugin is vibe coded. If you don't like it, then don't fucking use it.", Theme.TextSoft);
        Widgets.EndPanel(Theme.Mix(Theme.Card, Theme.Rose, 0.07f), Theme.Alpha(Theme.Rose, 0.45f), Theme.Rose);
        Widgets.VSpace(10f);

        // Links.
        ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
        if (Widgets.Button("View on GitHub", FontAwesomeIcon.CodeBranch))
            Util.OpenLink(Plugin.RepoUrl);
        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Report a problem", FontAwesomeIcon.Bug))
            Util.OpenLink(Plugin.RepoUrl + "/issues");
        Widgets.VSpace(14f);

        // Credits.
        ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
        Widgets.BeginPanel(column);
        Widgets.SectionLabel("Standing on the shoulders of", assets);
        Widgets.VSpace(2f);
        Credit("SubmarineTracker by Infi", "showed where the game keeps voyage results; SubLedger reads them the same way and can import its history.");
        Credit("Argus by ofnature", "showed that the result window alone is enough to notice a finished voyage.");
        Credit("BunnySubSync by Juusohel", "worked out how to tell one voyage from the next when the game clears the return time.");
        Credit("Dalamud and FFXIVClientStructs", "make all of this possible.");
        Widgets.VSpace(2f);
        Widgets.Text("All three are open source under the MIT licence, as is SubLedger.", Theme.TextDim);
        Widgets.EndPanel();
        Widgets.VSpace(10f);

        Centered("FINAL FANTASY XIV © SQUARE ENIX CO., LTD. SubLedger is a fan-made tool and is not affiliated with Square Enix.", Theme.TextMuted, left, column);
        Widgets.VSpace(10f);
    }

    private static void Credit(string name, string what)
    {
        var drawList = ImGui.GetWindowDrawList();
        var bullet = ImGui.GetCursorScreenPos();
        drawList.AddCircleFilled(bullet + new Vector2(Theme.S(5f), ImGui.GetTextLineHeight() * 0.5f), Theme.S(2.5f), Theme.U32(Theme.Gold));
        ImGui.SetCursorScreenPos(bullet + new Vector2(Theme.S(18f), 0));
        ImGui.BeginGroup();
        Widgets.Text(name, Theme.Text);
        Widgets.Text(what, Theme.TextDim);
        ImGui.EndGroup();
        Widgets.VSpace(3f);
    }

    /// <summary>Centres text in a column, wrapping it there when it is too long for one line.</summary>
    private static void Centered(string text, Vector4 color, float left, float width)
    {
        var size = ImGui.CalcTextSize(text);
        var y = ImGui.GetCursorScreenPos().Y;
        if (size.X <= width)
        {
            ImGui.SetCursorScreenPos(new Vector2(left + (width - size.X) * 0.5f, y));
            Widgets.Text(text, color);
            return;
        }

        ImGui.SetCursorScreenPos(new Vector2(left, y));
        ImGui.PushTextWrapPos(left + width - ImGui.GetWindowPos().X + ImGui.GetScrollX());
        Widgets.Text(text, color);
        ImGui.PopTextWrapPos();
    }
}
