using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core;

namespace SubLedger.Ui.Pages;

/// <summary>What SubLedger has been doing: every capture, import, backup and problem since the plugin loaded.</summary>
internal sealed class ConsolePage
{
    private static readonly (LogLevel Level, string Name, string Tag, Vector4 Color)[] Levels =
    [
        (LogLevel.Verbose, "Verbose", "VRBS", Theme.TextDim),
        (LogLevel.Debug, "Debug", "DBUG", Theme.Blue),
        (LogLevel.Info, "Info", "INFO", Theme.Teal),
        (LogLevel.Warning, "Warnings", "WARN", Theme.Amber),
        (LogLevel.Error, "Errors", "FAIL", Theme.Rose),
    ];

    private readonly Plugin plugin;
    private readonly bool[] shown = [false, true, true, true, true];
    private string search = string.Empty;
    private long cachedVersion = -1;
    private string cachedSearch = string.Empty;
    private int cachedMask = -1;
    private LogLine[] all = [];
    private List<LogLine> visible = [];
    private readonly int[] counts = new int[5];
    private bool stickToBottom = true;
    private DateTime copiedAt = DateTime.MinValue;

    public ConsolePage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private void Refresh()
    {
        var mask = 0;
        for (var i = 0; i < shown.Length; i++)
            mask |= shown[i] ? 1 << i : 0;

        var version = plugin.Log.Version;
        if (version == cachedVersion && search == cachedSearch && mask == cachedMask)
            return;

        cachedVersion = version;
        cachedSearch = search;
        cachedMask = mask;
        all = plugin.Log.Snapshot();

        Array.Clear(counts);
        visible = new List<LogLine>(all.Length);
        foreach (var line in all)
        {
            counts[(int)line.Level]++;
            if (!shown[(int)line.Level])
                continue;
            if (search.Length > 0
                && !line.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !line.Source.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            visible.Add(line);
        }
    }

    public void Draw()
    {
        Refresh();
        var assets = plugin.Assets;
        Widgets.PageHeader("Console", $"{all.Length} {(all.Length == 1 ? "line" : "lines")} since the plugin loaded. Copy this when reporting a problem.", assets);

        var buttonsWidth = Theme.S(250f);
        ImGui.SetNextItemWidth(MathF.Max(Theme.S(160f), ImGui.GetContentRegionAvail().X - buttonsWidth));
        ImGui.InputTextWithHint("##logSearch", "Search messages and sources", ref search, 96);
        ImGui.SameLine(0, Theme.S(10f));
        var copied = (DateTime.UtcNow - copiedAt).TotalSeconds < 2;
        if (Widgets.Button(copied ? "Copied" : "Copy log", copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, Widgets.ButtonKind.Primary))
        {
            ImGui.SetClipboardText(Format(visible));
            copiedAt = DateTime.UtcNow;
        }

        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Clear", FontAwesomeIcon.Eraser))
            plugin.Log.Clear();

        Widgets.VSpace(2f);
        for (var i = 0; i < Levels.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.S(8f));
            if (Widgets.FilterPill($"{Levels[i].Name}  {counts[i]}", Levels[i].Color, shown[i], assets))
                shown[i] = !shown[i];
        }

        Widgets.VSpace(4f);

        var footerHeight = ImGui.GetTextLineHeight() + Theme.S(10f);
        var size = new Vector2(0, MathF.Max(Theme.S(100f), ImGui.GetContentRegionAvail().Y - footerHeight));
        Widgets.BeginCard("##log", size, Theme.Mix(Theme.Abyss, Theme.Deep, 0.5f), flags: ImGuiWindowFlags.HorizontalScrollbar, padding: Theme.S(12f, 10f));

        var lineHeight = ImGui.GetTextLineHeight() + Theme.S(7f);
        var drawList = ImGui.GetWindowDrawList();
        var scrollY = ImGui.GetScrollY();
        var atBottom = scrollY >= ImGui.GetScrollMaxY() - Theme.S(4f);
        var origin = ImGui.GetCursorScreenPos();
        var viewHeight = ImGui.GetWindowHeight();
        var first = Math.Max(0, (int)(scrollY / lineHeight) - 1);
        var last = Math.Min(visible.Count - 1, (int)((scrollY + viewHeight) / lineHeight) + 1);

        var timeWidth = ImGui.CalcTextSize("00:00:00.000").X + Theme.S(12f);
        var tagWidth = Widgets.Measure("WARN", assets.Small).X + Theme.S(14f);
        var sourceWidth = ImGui.CalcTextSize("Valuation").X + Theme.S(14f);
        var widest = 0f;

        for (var i = first; i <= last; i++)
        {
            var line = visible[i];
            var level = Levels[(int)line.Level];
            var y = origin.Y + i * lineHeight;
            var x = origin.X;

            drawList.AddText(new Vector2(x, y), Theme.U32(Theme.TextMuted), line.Time.ToString("HH:mm:ss.fff"));
            x += timeWidth;

            var tagSize = Widgets.Measure(level.Tag, assets.Small);
            var tagMin = new Vector2(x, y - Theme.S(1f));
            var tagMax = new Vector2(x + tagWidth - Theme.S(6f), y + ImGui.GetTextLineHeight() + Theme.S(1f));
            drawList.AddRectFilled(tagMin, tagMax, Theme.U32(Theme.Mix(Theme.Card, level.Color, 0.20f)), Theme.S(4f));
            Widgets.TextAt(drawList, new Vector2(tagMin.X + (tagMax.X - tagMin.X - tagSize.X) * 0.5f, y + (ImGui.GetTextLineHeight() - tagSize.Y) * 0.5f), level.Tag, level.Color, assets.Small);
            x += tagWidth + Theme.S(4f);

            drawList.AddText(new Vector2(x, y), Theme.U32(Theme.TextDim), line.Source);
            x += sourceWidth;

            var color = line.Level switch
            {
                LogLevel.Error => Theme.Rose,
                LogLevel.Warning => Theme.Amber,
                LogLevel.Verbose => Theme.TextDim,
                _ => Theme.TextSoft,
            };
            drawList.AddText(new Vector2(x, y), Theme.U32(color), line.Message);
            widest = MathF.Max(widest, x - origin.X + ImGui.CalcTextSize(line.Message).X);
        }

        ImGui.Dummy(new Vector2(widest, visible.Count * lineHeight));

        // Follow new lines while the view is at the bottom; scrolling up stops the following until it is back there.
        if (stickToBottom || atBottom)
            ImGui.SetScrollHereY(1f);
        stickToBottom = false;

        Widgets.EndCard();

        Widgets.VSpace(2f);
        Widgets.Text($"Showing {visible.Count} of {all.Length}", Theme.TextDim);
    }

    private static string Format(IEnumerable<LogLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var level = Levels.First(l => l.Level == line.Level).Tag;
            sb.Append(line.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
              .Append(level).Append(' ')
              .Append('[').Append(line.Source).Append("] ")
              .AppendLine(line.Message);
        }

        return sb.ToString();
    }
}
