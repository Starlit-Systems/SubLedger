using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace SubLedger.Ui.Pages;

/// <summary>Every update, newest first.</summary>
internal sealed class UpdatesPage
{
    private readonly Plugin plugin;
    private string? seenAtOpen;

    public UpdatesPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var assets = plugin.Assets;

        // Remember what was unread when the page was opened, so the "new" markers stay up while it is being read.
        seenAtOpen ??= plugin.Config.LastSeenChangelog;
        if (plugin.Config.LastSeenChangelog != Changelog.Latest.Version)
        {
            plugin.Config.LastSeenChangelog = Changelog.Latest.Version;
            plugin.Config.Save();
        }

        Widgets.PageHeader("What's new", "Every update, newest first.", assets);

        var unread = true;
        var railX = ImGui.GetCursorScreenPos().X + Theme.S(9f);
        var indent = Theme.S(32f);
        var drawList = ImGui.GetWindowDrawList();

        for (var i = 0; i < Changelog.Entries.Count; i++)
        {
            var entry = Changelog.Entries[i];
            if (entry.Version == seenAtOpen)
                unread = false;

            var top = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(top + new Vector2(indent, 0));
            Widgets.BeginPanel(ImGui.GetContentRegionAvail().X);

            Widgets.Text($"Version {entry.Version}", i == 0 ? Theme.Gold : Theme.Text, assets.Heading);
            if (unread)
            {
                ImGui.SameLine(0, Theme.S(10f));
                Widgets.Pill("NEW", Theme.Teal, assets, dot: false);
            }

            var meta = $"{entry.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} · {entry.Changes.Length} {(entry.Changes.Length == 1 ? "change" : "changes")}";
            var metaWidth = ImGui.CalcTextSize(meta).X;
            ImGui.SameLine();
            // A couple of pixels short of the edge, so rounding can never push it over the wrap line.
            ImGui.SetCursorScreenPos(new Vector2(Widgets.PanelRight - metaWidth - Theme.S(3f), ImGui.GetCursorScreenPos().Y + Theme.S(3f)));
            Widgets.Text(meta, Theme.TextDim);

            Widgets.PanelDivider();
            foreach (var change in entry.Changes)
            {
                var bullet = ImGui.GetCursorScreenPos();
                drawList.AddCircleFilled(bullet + new Vector2(Theme.S(5f), ImGui.GetTextLineHeight() * 0.5f), Theme.S(2.5f), Theme.U32(Theme.Teal));
                ImGui.SetCursorScreenPos(bullet + new Vector2(Theme.S(18f), 0));
                Widgets.Text(change, Theme.TextSoft);
                Widgets.VSpace(1f);
            }

            Widgets.EndPanel(border: i == 0 ? Theme.Alpha(Theme.Gold, 0.45f) : null);
            var bottom = ImGui.GetCursorScreenPos().Y;

            // Timeline rail and marker beside the panel.
            var isLast = i == Changelog.Entries.Count - 1;
            var markerY = top.Y + Theme.S(26f);
            drawList.AddLine(new Vector2(railX, i == 0 ? markerY : top.Y - Theme.S(12f)), new Vector2(railX, isLast ? markerY : bottom + Theme.S(12f)), Theme.U32(Theme.Border));
            drawList.AddCircleFilled(new Vector2(railX, markerY), Theme.S(7f), Theme.U32(Theme.Abyss));
            drawList.AddCircle(new Vector2(railX, markerY), Theme.S(7f), Theme.U32(i == 0 ? Theme.Gold : Theme.Border));
            drawList.AddCircleFilled(new Vector2(railX, markerY), Theme.S(3.5f), Theme.U32(i == 0 ? Theme.Gold : Theme.TextDim));

            Widgets.VSpace(12f);
        }
    }
}
