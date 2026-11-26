using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using SubLedger.Core;

namespace SubLedger.Ui;

/// <summary>One point on the time axis. Missing means "no record at all", which is not the same as a zero.</summary>
/// <param name="AxisGroup">What the label belongs to (a month name, a year). Shown beside the label whenever it changes.</param>
internal readonly record struct ChartBar(string AxisLabel, string AxisGroup, long Value, bool Missing, string TooltipTitle, string TooltipDetail, bool Emphasis);

/// <summary>One row of a ranked breakdown.</summary>
internal readonly record struct RankedItem(string Label, long Value, string Detail);

/// <summary>
/// SubLedger's hand-drawn charts. One value axis per chart, thin marks, solid hairline grid, values on hover and at
/// the points that matter. Mark colours are the validated series steps in <see cref="Theme"/>; text stays in text
/// colours.
/// </summary>
internal static class BarChart
{
    private readonly record struct Plot(Vector2 Min, Vector2 Max, long Top, float Slot, int Hovered, float LabelHeight)
    {
        public float Height => Max.Y - Min.Y;

        public float X(int index) => Min.X + Slot * (index + 0.5f);

        public float Y(long value) => Max.Y - Height * Math.Clamp(value, 0, Top) / Top;
    }

    /// <summary>
    /// Columns over time, optionally with a trend line on the same axis and a dashed reference level.
    /// </summary>
    public static void Draw(Vector2 size, IReadOnlyList<ChartBar> bars, UiAssets assets, long referenceLine = 0, string? referenceLabel = null,
        IReadOnlyList<long>? line = null, string? barLegend = null, string? lineLegend = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);
        if (bars.Count == 0 || size.X < Theme.S(120f) || size.Y < Theme.S(80f))
            return;

        long max = Math.Max(referenceLine, 1);
        foreach (var bar in bars)
            max = Math.Max(max, bar.Value);
        if (line is not null)
        {
            foreach (var value in line)
                max = Math.Max(max, value);
        }

        // Two series need a legend; one is named by the chart's title.
        var legendHeight = 0f;
        if (line is not null && barLegend is not null && lineLegend is not null)
        {
            legendHeight = Theme.S(24f);
            DrawLegend(drawList, origin, barLegend, lineLegend, assets);
        }

        var plot = Frame(drawList, origin + new Vector2(0, legendHeight), size - new Vector2(0, legendHeight), bars, max, assets);

        // Never wider than 24 px, and always leaving a gap to the neighbour, so bars read as separate marks.
        var barWidth = MathF.Max(1.5f, MathF.Min(plot.Slot - Theme.S(2f), Theme.S(24f)));
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var centerX = plot.X(i);
            var isHovered = i == plot.Hovered;

            if (bar.Missing)
            {
                // No record for this day: an empty ring on the baseline, deliberately unlike a zero.
                drawList.AddCircle(new Vector2(centerX, plot.Max.Y - Theme.S(5f)), Theme.S(2.5f), Theme.U32(isHovered ? Theme.TextDim : Theme.TextMuted));
                continue;
            }

            var top = MathF.Min(plot.Y(bar.Value), plot.Max.Y - Theme.S(2f));
            var rounding = MathF.Min(Theme.S(4f), barWidth * 0.5f);
            drawList.AddRectFilled(
                new Vector2(centerX - barWidth * 0.5f, top), new Vector2(centerX + barWidth * 0.5f, plot.Max.Y),
                Theme.U32(isHovered ? Theme.Gold : Theme.SeriesGold), rounding, ImDrawFlags.RoundCornersTop);
        }

        if (referenceLine > 0)
        {
            var y = plot.Y(referenceLine);
            for (var x = plot.Min.X; x < plot.Max.X; x += Theme.S(9f))
                drawList.AddLine(new Vector2(x, y), new Vector2(MathF.Min(x + Theme.S(5f), plot.Max.X), y), Theme.U32(Theme.Alpha(Theme.SeriesTeal, 0.9f)));

            if (!string.IsNullOrEmpty(referenceLabel))
            {
                var textSize = Widgets.Measure(referenceLabel, assets.Small);
                var labelPos = new Vector2(plot.Max.X - textSize.X - Theme.S(6f), y - textSize.Y - Theme.S(3f));
                drawList.AddRectFilled(labelPos - Theme.S(4f, 1f), labelPos + textSize + Theme.S(4f, 1f), Theme.U32(Theme.Alpha(Theme.Abyss, 0.85f)), Theme.S(4f));
                Widgets.TextAt(drawList, labelPos, referenceLabel, Theme.TextSoft, assets.Small);
            }
        }

        if (line is not null && line.Count == bars.Count)
        {
            DrawLine(drawList, plot, line, Theme.SeriesTeal);
            if (plot.Hovered >= 0)
                Dot(drawList, new Vector2(plot.X(plot.Hovered), plot.Y(line[plot.Hovered])), Theme.SeriesTeal);
        }

        if (plot.Hovered >= 0)
        {
            var bar = bars[plot.Hovered];
            BeginTip(bar.TooltipTitle);
            if (bar.Missing)
                Widgets.Text("No record for this day", Theme.TextDim);
            else
                Widgets.Text($"{GilFormat.Exact(bar.Value)} gil", Theme.Text, assets.Heading);
            if (line is not null && lineLegend is not null && line.Count == bars.Count)
                Widgets.Text($"{lineLegend}: {GilFormat.Exact(line[plot.Hovered])} gil", Theme.TextSoft);
            if (!string.IsNullOrEmpty(bar.TooltipDetail))
                Widgets.Text(bar.TooltipDetail, Theme.TextDim);
            EndTip();
        }
    }

    /// <summary>
    /// A single line with a light wash beneath it, for a quantity that accumulates. The final value is written at the
    /// end of the line.
    /// </summary>
    public static void DrawArea(Vector2 size, IReadOnlyList<ChartBar> points, UiAssets assets)
    {
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);
        if (points.Count == 0 || size.X < Theme.S(120f) || size.Y < Theme.S(80f))
            return;

        long max = 1;
        foreach (var point in points)
            max = Math.Max(max, point.Value);

        var plot = Frame(drawList, origin, size, points, max, assets);

        var wash = Theme.U32(Theme.Alpha(Theme.SeriesGold, 0.12f));
        for (var i = 1; i < points.Count; i++)
        {
            var a = new Vector2(plot.X(i - 1), plot.Y(points[i - 1].Value));
            var b = new Vector2(plot.X(i), plot.Y(points[i].Value));
            drawList.AddQuadFilled(a, b, new Vector2(b.X, plot.Max.Y), new Vector2(a.X, plot.Max.Y), wash);
        }

        var values = new long[points.Count];
        for (var i = 0; i < points.Count; i++)
            values[i] = points[i].Value;
        DrawLine(drawList, plot, values, Theme.SeriesGold);

        // The end of the line carries its value.
        var last = points.Count - 1;
        var end = new Vector2(plot.X(last), plot.Y(values[last]));
        Dot(drawList, end, Theme.SeriesGold);
        var label = GilFormat.Short(values[last]);
        var labelSize = Widgets.Measure(label, assets.Small);
        var labelPos = new Vector2(MathF.Min(end.X + Theme.S(8f), plot.Max.X - labelSize.X), MathF.Max(plot.Min.Y, end.Y - labelSize.Y - Theme.S(8f)));
        if (labelPos.X < end.X + Theme.S(8f))
            labelPos.X = end.X - labelSize.X - Theme.S(10f);
        Widgets.TextAt(drawList, labelPos, label, Theme.Text, assets.Small);

        if (plot.Hovered >= 0)
        {
            var point = points[plot.Hovered];
            var x = plot.X(plot.Hovered);
            drawList.AddLine(new Vector2(x, plot.Min.Y), new Vector2(x, plot.Max.Y), Theme.U32(Theme.Border));
            Dot(drawList, new Vector2(x, plot.Y(point.Value)), Theme.SeriesGold);

            BeginTip(point.TooltipTitle);
            Widgets.Text($"{GilFormat.Exact(point.Value)} gil", Theme.Text, assets.Heading);
            if (!string.IsNullOrEmpty(point.TooltipDetail))
                Widgets.Text(point.TooltipDetail, Theme.TextDim);
            EndTip();
        }
    }

    /// <summary>
    /// Parts of a whole as horizontal bars on one baseline, largest first, each with its value and share at the tip.
    /// Draws in the current panel and takes as much height as it has rows.
    /// </summary>
    public static void DrawRanked(IReadOnlyList<RankedItem> items, long total, UiAssets assets)
    {
        if (items.Count == 0)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var left = Widgets.PanelLeft;
        var right = Widgets.PanelRight;
        var rowHeight = Theme.S(30f);
        var thickness = Theme.S(12f);

        var labelWidth = 0f;
        var valueWidth = 0f;
        long max = 1;
        foreach (var item in items)
        {
            labelWidth = MathF.Max(labelWidth, ImGui.CalcTextSize(item.Label).X);
            valueWidth = MathF.Max(valueWidth, ImGui.CalcTextSize(ValueText(item, total)).X);
            max = Math.Max(max, item.Value);
        }

        labelWidth = MathF.Min(labelWidth, (right - left) * 0.38f) + Theme.S(14f);
        var trackLeft = left + labelWidth;
        var trackWidth = MathF.Max(Theme.S(40f), right - trackLeft - valueWidth - Theme.S(12f));
        var mouse = ImGui.GetMousePos();
        var hoverable = ImGui.IsWindowHovered();

        foreach (var item in items)
        {
            var top = ImGui.GetCursorScreenPos().Y;
            var textY = top + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            var hovered = hoverable && mouse.Y >= top && mouse.Y < top + rowHeight && mouse.X >= left && mouse.X <= right;
            if (hovered)
                drawList.AddRectFilled(new Vector2(left - Theme.S(6f), top), new Vector2(right + Theme.S(6f), top + rowHeight), Theme.U32(Theme.Alpha(Theme.Teal, 0.06f)), Theme.S(5f));

            drawList.PushClipRect(new Vector2(left, top), new Vector2(trackLeft - Theme.S(8f), top + rowHeight), true);
            drawList.AddText(new Vector2(left, textY), Theme.U32(hovered ? Theme.Text : Theme.TextSoft), item.Label);
            drawList.PopClipRect();

            // Square at the baseline, rounded at the data end.
            var length = MathF.Max(Theme.S(2f), trackWidth * item.Value / max);
            var barTop = top + (rowHeight - thickness) * 0.5f;
            drawList.AddRectFilled(new Vector2(trackLeft, barTop), new Vector2(trackLeft + length, barTop + thickness),
                Theme.U32(hovered ? Theme.Gold : Theme.SeriesGold), MathF.Min(Theme.S(4f), length * 0.5f), ImDrawFlags.RoundCornersRight);

            drawList.AddText(new Vector2(trackLeft + length + Theme.S(8f), textY), Theme.U32(Theme.TextSoft), ValueText(item, total));

            ImGui.Dummy(new Vector2(right - left, rowHeight));
            if (hovered)
            {
                BeginTip(item.Label);
                Widgets.Text($"{GilFormat.Exact(item.Value)} gil", Theme.Text, assets.Heading);
                if (!string.IsNullOrEmpty(item.Detail))
                    Widgets.Text(item.Detail, Theme.TextDim);
                EndTip();
            }
        }
    }

    private static string ValueText(RankedItem item, long total)
    {
        var share = total <= 0 ? 0 : item.Value * 100.0 / total;
        var percent = share >= 10 ? share.ToString("0") : share >= 0.1 ? share.ToString("0.#") : "<0.1";
        return $"{GilFormat.Short(item.Value)}  ({percent}%)";
    }

    /// <summary>Draws the value axis, its hairline guides and the time labels, and works out which point the mouse is over.</summary>
    private static Plot Frame(ImDrawListPtr drawList, Vector2 origin, Vector2 size, IReadOnlyList<ChartBar> points, long max, UiAssets assets)
    {
        var (step, steps) = NiceScale(max);
        var top = step * steps;

        var axisWidth = Widgets.Measure(GilFormat.Short(top), assets.Small).X + Theme.S(10f);
        var labelHeight = Widgets.Measure("0", assets.Small).Y + Theme.S(6f);
        var plotMin = origin + new Vector2(axisWidth, Theme.S(6f));
        var plotMax = origin + new Vector2(size.X, size.Y - labelHeight);
        var height = plotMax.Y - plotMin.Y;

        for (var i = 0; i <= steps; i++)
        {
            var y = plotMax.Y - height * i / steps;
            drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), Theme.U32(i == 0 ? Theme.Border : Theme.Hairline));
            var text = GilFormat.Short(step * i);
            var textSize = Widgets.Measure(text, assets.Small);
            Widgets.TextAt(drawList, new Vector2(plotMin.X - textSize.X - Theme.S(8f), y - textSize.Y * 0.5f), text, Theme.TextMuted, assets.Small);
        }

        var slot = (plotMax.X - plotMin.X) / points.Count;
        var hovered = -1;
        var mouse = ImGui.GetMousePos();
        if (ImGui.IsWindowHovered() && mouse.X >= plotMin.X && mouse.X < plotMax.X && mouse.Y >= plotMin.Y - Theme.S(4f) && mouse.Y <= plotMax.Y + labelHeight)
        {
            hovered = Math.Clamp((int)((mouse.X - plotMin.X) / slot), 0, points.Count - 1);
            drawList.AddRectFilled(new Vector2(plotMin.X + slot * hovered, plotMin.Y), new Vector2(plotMin.X + slot * (hovered + 1), plotMax.Y), Theme.U32(Theme.Alpha(Theme.Teal, 0.07f)));
        }

        // Roughly one label per 64 pixels so they never collide; the month or year is named whenever it changes.
        var labelEvery = Math.Max(1, (int)MathF.Ceiling(Theme.S(64f) / slot));
        var lastGroup = string.Empty;
        for (var i = 0; i < points.Count; i += labelEvery)
        {
            var point = points[i];
            var newGroup = point.AxisGroup.Length > 0 && point.AxisGroup != lastGroup;
            lastGroup = point.AxisGroup;
            var label = newGroup ? $"{point.AxisGroup} {point.AxisLabel}" : point.AxisLabel;
            var textSize = Widgets.Measure(label, assets.Small);
            var centerX = plotMin.X + slot * (i + 0.5f);
            var x = Math.Clamp(centerX - textSize.X * 0.5f, plotMin.X, MathF.Max(plotMin.X, plotMax.X - textSize.X));
            Widgets.TextAt(drawList, new Vector2(x, plotMax.Y + Theme.S(5f)), label, i == hovered || newGroup ? Theme.TextSoft : Theme.TextMuted, assets.Small);
        }

        return new Plot(plotMin, plotMax, top, slot, hovered, labelHeight);
    }

    private static void DrawLine(ImDrawListPtr drawList, Plot plot, IReadOnlyList<long> values, Vector4 color)
    {
        var ink = Theme.U32(color);
        for (var i = 1; i < values.Count; i++)
        {
            drawList.AddLine(
                new Vector2(plot.X(i - 1), plot.Y(values[i - 1])),
                new Vector2(plot.X(i), plot.Y(values[i])),
                ink, Theme.S(2f));
        }
    }

    /// <summary>A marker with a ring in the surface colour, so it stays readable where it crosses other marks.</summary>
    private static void Dot(ImDrawListPtr drawList, Vector2 center, Vector4 color)
    {
        drawList.AddCircleFilled(center, Theme.S(6f), Theme.U32(Theme.Card), 20);
        drawList.AddCircleFilled(center, Theme.S(4f), Theme.U32(color), 20);
    }

    private static void DrawLegend(ImDrawListPtr drawList, Vector2 origin, string barLegend, string lineLegend, UiAssets assets)
    {
        var textHeight = Widgets.Measure("A", assets.Small).Y;
        var y = origin.Y + Theme.S(2f);
        var x = origin.X + Theme.S(2f);
        var mid = y + textHeight * 0.5f;

        drawList.AddRectFilled(new Vector2(x, mid - Theme.S(5f)), new Vector2(x + Theme.S(10f), mid + Theme.S(5f)), Theme.U32(Theme.SeriesGold), Theme.S(2f));
        x += Theme.S(16f);
        Widgets.TextAt(drawList, new Vector2(x, y), barLegend, Theme.TextSoft, assets.Small);
        x += Widgets.Measure(barLegend, assets.Small).X + Theme.S(18f);

        drawList.AddLine(new Vector2(x, mid), new Vector2(x + Theme.S(16f), mid), Theme.U32(Theme.SeriesTeal), Theme.S(2f));
        x += Theme.S(22f);
        Widgets.TextAt(drawList, new Vector2(x, y), lineLegend, Theme.TextSoft, assets.Small);
    }

    private static void BeginTip(string title)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(11f, 9f));
        ImGui.BeginTooltip();
        Widgets.Text(title, Theme.TextSoft);
    }

    private static void EndTip()
    {
        ImGui.EndTooltip();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Picks the axis: three to five equal steps of a round size (1, 2, 2.5 or 5 times a power of ten) that reach the
    /// largest value with the least empty space above it.
    /// </summary>
    internal static (long Step, int Steps) NiceScale(long max)
    {
        if (max <= 4)
            return (1, 4);

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        (long Step, int Steps) best = ((long)(magnitude * 10), 3);
        var bestTop = double.MaxValue;
        foreach (var scale in new[] { magnitude / 10, magnitude, magnitude * 10 })
        {
            foreach (var mantissa in new[] { 1.0, 2.0, 2.5, 5.0 })
            {
                var step = Math.Round(scale * mantissa);
                if (step < 1)
                    continue;

                var steps = (int)Math.Ceiling(max / step);
                if (steps is < 3 or > 5)
                    continue;

                var top = step * steps;
                if (top < bestTop)
                {
                    bestTop = top;
                    best = ((long)step, steps);
                }
            }
        }

        return best;
    }
}
