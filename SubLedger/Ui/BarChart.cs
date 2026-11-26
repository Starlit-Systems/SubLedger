using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using SubLedger.Core;

namespace SubLedger.Ui;

/// <summary>One bar. A missing bar is a day with no record at all, which is not the same as a day that earned zero.</summary>
internal readonly record struct ChartBar(string AxisLabel, long Value, bool Missing, string TooltipTitle, string TooltipDetail, bool Emphasis);

/// <summary>A hand-drawn bar chart in the ledger's colours, with hover details.</summary>
internal static class BarChart
{
    public static void Draw(Vector2 size, IReadOnlyList<ChartBar> bars, UiAssets assets, long referenceLine = 0, string? referenceLabel = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);
        if (bars.Count == 0 || size.X < Theme.S(120f) || size.Y < Theme.S(80f))
            return;

        long max = Math.Max(referenceLine, 1);
        foreach (var bar in bars)
            max = Math.Max(max, bar.Value);
        var top = NiceCeiling(max);

        var axisWidth = Widgets.Measure(GilFormat.Short(top), assets.Small).X + Theme.S(10f);
        var labelHeight = Widgets.Measure("0", assets.Small).Y + Theme.S(6f);
        var plotMin = origin + new Vector2(axisWidth, Theme.S(6f));
        var plotMax = origin + new Vector2(size.X, size.Y - labelHeight);
        var plotHeight = plotMax.Y - plotMin.Y;
        var plotWidth = plotMax.X - plotMin.X;

        // Horizontal guides with their values.
        const int steps = 4;
        for (var i = 0; i <= steps; i++)
        {
            var value = top * i / steps;
            var y = plotMax.Y - plotHeight * i / steps;
            drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), Theme.U32(i == 0 ? Theme.Border : Theme.Hairline));
            var text = GilFormat.Short(value);
            var textSize = Widgets.Measure(text, assets.Small);
            Widgets.TextAt(drawList, new Vector2(plotMin.X - textSize.X - Theme.S(8f), y - textSize.Y * 0.5f), text, Theme.TextMuted, assets.Small);
        }

        var slot = plotWidth / bars.Count;
        var barWidth = MathF.Max(2f, MathF.Min(slot * 0.68f, Theme.S(34f)));
        var hovered = -1;
        var mouse = ImGui.GetMousePos();
        if (ImGui.IsWindowHovered() && mouse.X >= plotMin.X && mouse.X < plotMax.X && mouse.Y >= plotMin.Y - Theme.S(4f) && mouse.Y <= plotMax.Y + labelHeight)
            hovered = Math.Clamp((int)((mouse.X - plotMin.X) / slot), 0, bars.Count - 1);

        // Show roughly one axis label per 56 pixels so they never collide.
        var labelEvery = Math.Max(1, (int)MathF.Ceiling(Theme.S(56f) / slot));

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var centerX = plotMin.X + slot * (i + 0.5f);
            var isHovered = i == hovered;

            if (isHovered)
                drawList.AddRectFilled(new Vector2(plotMin.X + slot * i, plotMin.Y), new Vector2(plotMin.X + slot * (i + 1), plotMax.Y), Theme.U32(Theme.Alpha(Theme.Teal, 0.07f)));

            if (bar.Missing)
            {
                // No record for this day: an empty ring on the baseline, deliberately unlike a zero.
                drawList.AddCircle(new Vector2(centerX, plotMax.Y - Theme.S(5f)), Theme.S(2.5f), Theme.U32(isHovered ? Theme.TextDim : Theme.TextMuted));
            }
            else
            {
                var height = MathF.Max(Theme.S(2f), plotHeight * bar.Value / top);
                var barMin = new Vector2(centerX - barWidth * 0.5f, plotMax.Y - height);
                var barMax = new Vector2(centerX + barWidth * 0.5f, plotMax.Y);
                var body = bar.Emphasis ? Theme.GoldBright : Theme.Gold;
                if (isHovered)
                    body = Theme.GoldBright;

                var rounding = MathF.Min(Theme.S(4f), barWidth * 0.5f);
                drawList.AddRectFilled(barMin, barMax, Theme.U32(Theme.Mix(Theme.GoldDeep, body, 0.55f)), rounding, ImDrawFlags.RoundCornersTop);
                // Brighter upper half, so each bar reads as lit from above.
                if (height > Theme.S(8f))
                    drawList.AddRectFilled(barMin, new Vector2(barMax.X, barMin.Y + height * 0.5f), Theme.U32(Theme.Alpha(body, 0.85f)), rounding, ImDrawFlags.RoundCornersTop);
            }

            if (i % labelEvery == 0 || i == bars.Count - 1 && labelEvery == 1)
            {
                var textSize = Widgets.Measure(bar.AxisLabel, assets.Small);
                Widgets.TextAt(drawList, new Vector2(centerX - textSize.X * 0.5f, plotMax.Y + Theme.S(5f)), bar.AxisLabel,
                    isHovered ? Theme.TextSoft : Theme.TextMuted, assets.Small);
            }
        }

        if (referenceLine > 0)
        {
            var y = plotMax.Y - plotHeight * referenceLine / top;
            for (var x = plotMin.X; x < plotMax.X; x += Theme.S(9f))
                drawList.AddLine(new Vector2(x, y), new Vector2(MathF.Min(x + Theme.S(5f), plotMax.X), y), Theme.U32(Theme.Alpha(Theme.Teal, 0.75f)));

            if (!string.IsNullOrEmpty(referenceLabel))
            {
                var textSize = Widgets.Measure(referenceLabel, assets.Small);
                var labelPos = new Vector2(plotMax.X - textSize.X - Theme.S(6f), y - textSize.Y - Theme.S(3f));
                drawList.AddRectFilled(labelPos - Theme.S(4f, 1f), labelPos + textSize + Theme.S(4f, 1f), Theme.U32(Theme.Alpha(Theme.Abyss, 0.85f)), Theme.S(4f));
                Widgets.TextAt(drawList, labelPos, referenceLabel, Theme.Teal, assets.Small);
            }
        }

        if (hovered >= 0)
        {
            var bar = bars[hovered];
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(11f, 9f));
            ImGui.BeginTooltip();
            Widgets.Text(bar.TooltipTitle, Theme.TextSoft);
            if (bar.Missing)
                Widgets.Text("No record for this day", Theme.TextDim);
            else
                Widgets.Text($"{GilFormat.Exact(bar.Value)} gil", Theme.Gold, assets.Heading);
            if (!string.IsNullOrEmpty(bar.TooltipDetail))
                Widgets.Text(bar.TooltipDetail, Theme.TextDim);
            ImGui.EndTooltip();
            ImGui.PopStyleVar();
        }
    }

    /// <summary>Rounds up to 1, 2, 2.5, 5 or 10 times a power of ten, so the axis has tidy values.</summary>
    private static long NiceCeiling(long value)
    {
        if (value <= 4)
            return 4;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var fraction = value / magnitude;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 4 ? 4 : fraction <= 5 ? 5 : fraction <= 8 ? 8 : 10;
        return (long)(nice * magnitude);
    }
}
