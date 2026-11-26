using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core;
using SubLedger.Core.Model;

namespace SubLedger.Ui.Pages;

/// <summary>Every voyage in the ledger, newest first, with the loot behind each number.</summary>
internal sealed class VoyagesPage
{
    private sealed record Row(Voyage Voyage, string When, string Loot, string Value, string SearchText);

    private readonly Plugin plugin;
    private string search = string.Empty;
    private (long Version, ulong Fc, string Search, string Zone, int Cutoff) cacheKey;
    private List<Row> rows = [];
    private long filteredGil;
    private string? selectedId;

    public VoyagesPage(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private void Refresh()
    {
        var snapshot = plugin.Store.Snapshot();
        var calendar = plugin.Calendar;
        var key = (snapshot.Version, plugin.Config.SelectedFreeCompany, search, calendar.Zone.Id, calendar.CutoffMinutes);
        if (key == cacheKey)
            return;

        cacheKey = key;
        var fc = plugin.Config.SelectedFreeCompany;
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        rows = [];
        filteredGil = 0;
        foreach (var v in snapshot.Voyages)
        {
            if (fc != 0 && v.FcId != fc)
                continue;

            var when = calendar.ToLocal(v.CollectedUnix).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            var loot = string.Join(", ", v.Loot.OrderByDescending(l => l.TotalGil).Take(3).Select(l => $"{l.Quantity}× {l.ItemName}"));
            if (v.Loot.Count > 3)
                loot += $" +{v.Loot.Count - 3} more";

            var haystack = $"{v.SubName} {v.Route} {when} {string.Join(' ', v.Loot.Select(l => l.ItemName))}";
            if (terms.Length > 0 && !terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            rows.Add(new Row(v, when, loot, GilFormat.Exact(v.TotalGil), haystack));
            filteredGil += v.TotalGil;
        }

        if (selectedId is not null && rows.All(r => r.Voyage.Id != selectedId))
            selectedId = null;
    }

    public void Draw()
    {
        Refresh();
        var assets = plugin.Assets;
        Widgets.PageHeader("Voyages", "Every voyage in the ledger, newest first. Click one to see what it brought back.", assets);

        ImGui.SetNextItemWidth(Theme.S(320f));
        ImGui.InputTextWithHint("##search", "Search submarine, route or item", ref search, 96);
        ImGui.SameLine(0, Theme.S(14f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text($"{rows.Count.ToString("N0", CultureInfo.InvariantCulture)} voyages · {GilFormat.Exact(filteredGil)} gil", Theme.TextDim);
        Widgets.VSpace(4f);

        var available = ImGui.GetContentRegionAvail();
        var selected = selectedId is null ? null : rows.FirstOrDefault(r => r.Voyage.Id == selectedId);
        var detailWidth = selected is null ? 0f : MathF.Min(Theme.S(340f), available.X * 0.42f);
        var gap = selected is null ? 0f : Theme.S(10f);
        var listSize = new Vector2(available.X - detailWidth - gap, MathF.Max(Theme.S(120f), available.Y - Theme.S(2f)));

        DrawList(listSize, assets);

        if (selected is not null)
        {
            ImGui.SameLine(0, gap);
            DrawDetail(selected.Voyage, new Vector2(detailWidth, listSize.Y), assets);
        }
    }

    private void DrawList(Vector2 size, UiAssets assets)
    {
        // No padding: rows and the header are positioned by hand and run edge to edge.
        Widgets.BeginCard("##voyageList", size, flags: ImGuiWindowFlags.None, padding: Vector2.Zero);

        var drawList = ImGui.GetWindowDrawList();
        var windowPos = ImGui.GetWindowPos();
        var width = ImGui.GetWindowWidth() - (ImGui.GetScrollMaxY() > 0 ? Theme.S(12f) : 0f);
        var rowHeight = Theme.S(36f);
        var headerHeight = Theme.S(34f);
        var pad = Theme.S(14f);

        // Column positions, as fractions of the usable width.
        var inner = width - pad * 2;
        var valueWidth = Theme.S(118f);
        var routeWidth = Theme.S(70f);
        var whenWidth = MathF.Min(Theme.S(196f), inner * 0.30f);
        var flexible = MathF.Max(Theme.S(80f), inner - valueWidth - routeWidth - whenWidth);
        var subWidth = flexible * 0.34f;
        var lootWidth = flexible - subWidth;
        var xWhen = pad;
        var xSub = xWhen + whenWidth;
        var xRoute = xSub + subWidth;
        var xLoot = xRoute + routeWidth;
        var xValueRight = width - pad;

        // Header row, pinned to the top of the list while it scrolls.
        var scrollY = ImGui.GetScrollY();
        var headerTop = windowPos.Y;
        var textHeight = Widgets.Measure("A", assets.Small).Y;

        // One clickable area over all the rows. It takes mouse presses for the list (so clicking a row never drags the
        // window) and reserves the full scroll height.
        var hoveringRows = false;
        var clickedRows = false;
        ImGui.SetCursorPos(new Vector2(0, headerHeight));
        if (rows.Count == 0)
        {
            ImGui.SetCursorPos(new Vector2(pad, headerHeight + Theme.S(18f)));
            Widgets.Text(search.Length > 0 ? "No voyages match that search." : "No voyages recorded yet.", Theme.TextDim);
        }
        else
        {
            clickedRows = ImGui.InvisibleButton("##rows", new Vector2(MathF.Max(1f, width), rows.Count * rowHeight));
            hoveringRows = ImGui.IsItemHovered();
        }

        // Only the rows on screen are drawn, so a ledger with thousands of voyages stays smooth.
        var viewHeight = ImGui.GetWindowHeight();
        var first = Math.Max(0, (int)((scrollY - headerHeight) / rowHeight));
        var last = Math.Min(rows.Count - 1, (int)((scrollY + viewHeight) / rowHeight) + 1);
        var mouse = ImGui.GetMousePos();

        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var top = windowPos.Y + headerHeight + i * rowHeight - scrollY;
            var min = new Vector2(windowPos.X, top);
            var max = new Vector2(windowPos.X + width, top + rowHeight);
            if (max.Y < headerTop + headerHeight)
                continue;

            var isSelected = row.Voyage.Id == selectedId;
            var isHovered = hoveringRows && mouse.Y >= MathF.Max(min.Y, headerTop + headerHeight) && mouse.Y < max.Y && mouse.X >= min.X && mouse.X < max.X;

            if (isSelected)
            {
                drawList.AddRectFilled(min, max, Theme.U32(Theme.Mix(Theme.Card, Theme.Gold, 0.12f)));
                drawList.AddRectFilled(min, new Vector2(min.X + Theme.S(3f), max.Y), Theme.U32(Theme.Gold));
            }
            else if (isHovered)
            {
                drawList.AddRectFilled(min, max, Theme.U32(Theme.CardRaised));
            }

            drawList.AddLine(new Vector2(min.X + pad, max.Y), new Vector2(max.X - pad, max.Y), Theme.U32(Theme.Hairline));

            var textY = top + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            Clipped(drawList, windowPos.X + xWhen, textY, whenWidth - Theme.S(10f), row.When, Theme.TextSoft, rowHeight);
            Clipped(drawList, windowPos.X + xSub, textY, subWidth - Theme.S(10f), string.IsNullOrEmpty(row.Voyage.SubName) ? "Submarine" : row.Voyage.SubName, Theme.Text, rowHeight);
            Clipped(drawList, windowPos.X + xRoute, textY, routeWidth - Theme.S(8f), row.Voyage.Route, Theme.Teal, rowHeight);
            Clipped(drawList, windowPos.X + xLoot, textY, lootWidth - Theme.S(12f), row.Loot, Theme.TextDim, rowHeight);
            var valueSize = ImGui.CalcTextSize(row.Value);
            drawList.AddText(new Vector2(windowPos.X + xValueRight - valueSize.X, textY), Theme.U32(Theme.Gold), row.Value);

            if (isHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (clickedRows)
                    selectedId = isSelected ? null : row.Voyage.Id;
            }
        }

        // Reserve the full scroll height.
        ImGui.SetCursorPos(new Vector2(0, headerHeight + rows.Count * rowHeight));
        ImGui.Dummy(new Vector2(1, Theme.S(4f)));

        // The header is drawn last so rows scroll underneath it.
        var headerMin = new Vector2(windowPos.X + 1f, headerTop + 1f);
        var headerMax = new Vector2(windowPos.X + width - 1f, headerTop + headerHeight);
        drawList.AddRectFilled(headerMin, headerMax, Theme.U32(Theme.Mix(Theme.Card, Theme.Deep, 0.55f)), Theme.S(8f), ImDrawFlags.RoundCornersTop);
        drawList.AddLine(new Vector2(headerMin.X, headerMax.Y), headerMax, Theme.U32(Theme.BorderSoft));
        var headerTextY = headerTop + (headerHeight - textHeight) * 0.5f;
        Widgets.TextAt(drawList, new Vector2(windowPos.X + xWhen, headerTextY), "COLLECTED", Theme.TextDim, assets.Small);
        Widgets.TextAt(drawList, new Vector2(windowPos.X + xSub, headerTextY), "SUBMARINE", Theme.TextDim, assets.Small);
        Widgets.TextAt(drawList, new Vector2(windowPos.X + xRoute, headerTextY), "ROUTE", Theme.TextDim, assets.Small);
        Widgets.TextAt(drawList, new Vector2(windowPos.X + xLoot, headerTextY), "LOOT", Theme.TextDim, assets.Small);
        var valueHeader = Widgets.Measure("VALUE (GIL)", assets.Small);
        Widgets.TextAt(drawList, new Vector2(windowPos.X + xValueRight - valueHeader.X, headerTextY), "VALUE (GIL)", Theme.TextDim, assets.Small);

        Widgets.EndCard();
    }

    /// <summary>Text cut off cleanly at a column's right edge.</summary>
    private static void Clipped(ImDrawListPtr drawList, float x, float y, float width, string text, Vector4 color, float rowHeight)
    {
        if (width <= 0)
            return;

        drawList.PushClipRect(new Vector2(x, y - rowHeight), new Vector2(x + width, y + rowHeight), true);
        drawList.AddText(new Vector2(x, y), Theme.U32(color), text);
        drawList.PopClipRect();
    }

    private void DrawDetail(Voyage v, Vector2 size, UiAssets assets)
    {
        Widgets.BeginCard("##voyageDetail", size, flags: ImGuiWindowFlags.None);
        var calendar = plugin.Calendar;

        Widgets.Text(string.IsNullOrEmpty(v.SubName) ? "Submarine" : v.SubName, Theme.Text, assets.Heading);
        var fc = plugin.Store.Snapshot().FreeCompanies.FirstOrDefault(c => c.FcId == v.FcId);
        if (fc is not null)
            Widgets.Text(fc.DisplayName, Theme.TextDim);
        Widgets.VSpace(2f);

        Widgets.Text(GilFormat.Exact(v.TotalGil), Theme.Gold, assets.Figure);
        ImGui.SameLine(0, Theme.S(6f));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Widgets.Measure("0", assets.Figure).Y - ImGui.GetTextLineHeight() - Theme.S(5f));
        Widgets.Text("gil", Theme.TextDim);
        Widgets.VSpace(2f);

        Widgets.Pill($"ROUTE {v.Route}", Theme.Teal, assets, dot: false);
        ImGui.SameLine(0, Theme.S(6f));
        if (v.Source == VoyageSource.SubmarineTracker)
            Widgets.Pill("IMPORTED", Theme.Blue, assets, dot: false);
        else
            Widgets.Pill("RECORDED LIVE", Theme.Mint, assets, dot: false);
        Widgets.VSpace(6f);

        Fact("Collected", calendar.ToLocal(v.CollectedUnix).ToString("ddd d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture));
        Fact("Ledger day", calendar.DateOf(v.CollectedUnix).ToString("d MMM yyyy", CultureInfo.InvariantCulture));
        if (v.ReturnUnix != 0)
            Fact("Returned", calendar.ToLocal(v.ReturnUnix).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture));
        if (v.DispatchedUnix != 0)
        {
            Fact("Sent out", calendar.ToLocal(v.DispatchedUnix).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture));
            if (v.ReturnUnix > v.DispatchedUnix)
                Fact("Voyage length", Duration(v.ReturnUnix - v.DispatchedUnix));
        }

        Fact("Source", v.Source == VoyageSource.SubmarineTracker ? "SubmarineTracker history" : $"Seen by SubLedger ({v.Trigger})");

        Widgets.VSpace(6f);
        ImGui.Separator();
        Widgets.VSpace(4f);

        foreach (var sector in v.Loot.GroupBy(l => l.SectorId))
        {
            var letter = plugin.Sectors.Letter(sector.Key);
            var name = plugin.Sectors.Name(sector.Key);
            Widgets.Text(string.IsNullOrEmpty(name) ? $"Sector {letter}" : name, Theme.TextDim, assets.Small);

            foreach (var line in sector)
            {
                Widgets.Text($"{line.Quantity}×", Theme.TextDim);
                ImGui.SameLine(0, Theme.S(6f));
                Widgets.Text(line.Hq ? $"{line.ItemName} (HQ)" : line.ItemName, Theme.Text);
                ImGui.SameLine();
                Widgets.TextRight(GilFormat.Exact(line.TotalGil), line.TotalGil >= 1000 ? Theme.Gold : Theme.TextSoft);
                Widgets.Tooltip($"{line.Quantity} × {GilFormat.Exact(line.UnitPrice)} gil each at an NPC vendor");
            }

            Widgets.VSpace(4f);
        }

        Widgets.VSpace(6f);
        if (Widgets.Button("Remove from ledger", FontAwesomeIcon.Trash, Widgets.ButtonKind.Danger))
        {
            if (ImGui.GetIO().KeyCtrl)
            {
                var id = v.Id;
                var label = $"'{v.SubName}' {v.Route}, {GilFormat.Exact(v.TotalGil)} gil";
                plugin.Worker.Enqueue("Remove voyage", () =>
                {
                    if (plugin.Store.SetVoyageDeleted(id, true))
                        plugin.Log.Info("Ledger", $"Voyage removed from the ledger by hand: {label}.");
                });
                selectedId = null;
            }
        }

        Widgets.Tooltip("Hold Ctrl and click. The voyage leaves every total; it is kept on file so it is never recorded again.");
        Widgets.EndCard();
    }

    private static void Fact(string label, string value)
    {
        Widgets.Text(label, Theme.TextDim);
        ImGui.SameLine();
        Widgets.TextRight(value, Theme.TextSoft);
    }

    private static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m"
            : $"{span.Hours}h {span.Minutes}m";
    }
}
