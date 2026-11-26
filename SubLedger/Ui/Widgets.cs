using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace SubLedger.Ui;

/// <summary>SubLedger's hand-drawn controls. Each draws at the cursor and advances it like a normal ImGui item.</summary>
internal static class Widgets
{
    public static void Text(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void Text(string text, Vector4 color, IFontHandle font)
    {
        using (font.Push())
            Text(text, color);
    }

    public static void TextWrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0f);
        Text(text, color);
        ImGui.PopTextWrapPos();
    }

    public static Vector2 Measure(string text, IFontHandle? font = null)
    {
        if (font is null)
            return ImGui.CalcTextSize(text);

        using (font.Push())
            return ImGui.CalcTextSize(text);
    }

    /// <summary>Draws text at an exact screen position without moving the cursor.</summary>
    public static void TextAt(ImDrawListPtr drawList, Vector2 position, string text, Vector4 color, IFontHandle? font = null)
    {
        if (font is null)
        {
            drawList.AddText(position, Theme.U32(color), text);
            return;
        }

        using (font.Push())
            drawList.AddText(position, Theme.U32(color), text);
    }

    public static void VSpace(float pixels) => ImGui.Dummy(new Vector2(0, Theme.S(pixels)));

    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using (Service.PluginInterface.UiBuilder.IconFontHandle.Push())
            Text(icon.ToIconString(), color);
    }

    public static Vector2 IconSize(FontAwesomeIcon icon)
    {
        using (Service.PluginInterface.UiBuilder.IconFontHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString());
    }

    public static void IconAt(ImDrawListPtr drawList, Vector2 position, FontAwesomeIcon icon, Vector4 color)
    {
        using (Service.PluginInterface.UiBuilder.IconFontHandle.Push())
            drawList.AddText(position, Theme.U32(color), icon.ToIconString());
    }

    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.S(10f, 8f));
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(Theme.S(340f));
        Text(text, Theme.TextSoft);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleVar();
    }

    /// <summary>Small dim capitals above a group of things.</summary>
    public static void SectionLabel(string label, UiAssets assets)
    {
        Text(label.ToUpperInvariant(), Theme.TextDim, assets.Small);
    }

    /// <summary>A page's title and one line saying what the page is for.</summary>
    public static void PageHeader(string title, string subtitle, UiAssets assets)
    {
        Text(title, Theme.Text, assets.Title);
        Text(subtitle, Theme.TextDim);
        VSpace(8f);
    }

    /// <summary>
    /// Starts a rounded panel. Always pair with <see cref="EndCard"/>, whatever this returns.
    /// </summary>
    public static bool BeginCard(string id, Vector2 size, Vector4? background = null, Vector4? border = null,
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, Vector2? padding = null)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.S(9f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding ?? Theme.S(14f, 12f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background ?? Theme.Card);
        ImGui.PushStyleColor(ImGuiCol.Border, border ?? Theme.BorderSoft);
        return ImGui.BeginChild(id, size, true, flags);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    private static Vector2 panelOrigin;
    private static float panelWidth;
    private static Vector2 panelPadding;

    /// <summary>Screen x of the left edge of the current panel's content.</summary>
    public static float PanelLeft => panelOrigin.X + panelPadding.X;

    /// <summary>Screen x of the right edge of the current panel's content.</summary>
    public static float PanelRight => panelOrigin.X + panelWidth - panelPadding.X;

    /// <summary>
    /// Starts a rounded panel that grows to fit whatever is drawn in it. The background is painted afterwards, behind
    /// the content, by <see cref="EndPanel"/>. Panels cannot be nested.
    /// </summary>
    public static void BeginPanel(float width = 0f)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        panelOrigin = ImGui.GetCursorScreenPos();
        panelWidth = width > 0f ? width : ImGui.GetContentRegionAvail().X;
        panelPadding = Theme.S(16f, 14f);

        ImGui.BeginGroup();
        ImGui.SetCursorScreenPos(panelOrigin + panelPadding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(PanelRight - ImGui.GetWindowPos().X + ImGui.GetScrollX());
    }

    public static void EndPanel(Vector4? background = null, Vector4? border = null, Vector4? accent = null)
    {
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var bottom = ImGui.GetItemRectMax().Y + panelPadding.Y;
        var end = new Vector2(panelOrigin.X + panelWidth, bottom);
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(panelOrigin, end, Theme.U32(background ?? Theme.Card), Theme.S(9f));
        drawList.AddRect(panelOrigin, end, Theme.U32(border ?? Theme.BorderSoft), Theme.S(9f));
        if (accent is { } color)
            drawList.AddRectFilled(panelOrigin + new Vector2(0, Theme.S(10f)), new Vector2(panelOrigin.X + Theme.S(3f), bottom - Theme.S(10f)), Theme.U32(color), Theme.S(2f));
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(panelOrigin.X, bottom));
        ImGui.Dummy(new Vector2(panelWidth, 0f));
        ImGui.EndGroup();
    }

    /// <summary>A thin line across the current panel, between rows.</summary>
    public static void PanelDivider()
    {
        var y = ImGui.GetCursorScreenPos().Y + Theme.S(4f);
        ImGui.GetWindowDrawList().AddLine(new Vector2(PanelLeft, y), new Vector2(PanelRight, y), Theme.U32(Theme.Hairline));
        ImGui.Dummy(new Vector2(0, Theme.S(9f)));
    }

    /// <summary>
    /// One setting inside a panel: its name and a plain explanation on the left, its control on the right.
    /// </summary>
    public static void SettingRow(string label, string? description, float controlWidth, float controlHeight, System.Action control)
    {
        var start = ImGui.GetCursorScreenPos();
        var textRight = PanelRight - controlWidth - Theme.S(24f);

        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(textRight - ImGui.GetWindowPos().X + ImGui.GetScrollX());
        Text(label, Theme.Text);
        if (!string.IsNullOrEmpty(description))
            Text(description, Theme.TextDim);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var textHeight = ImGui.GetItemRectSize().Y;
        var rowHeight = System.MathF.Max(textHeight, controlHeight);

        ImGui.SetCursorScreenPos(new Vector2(PanelRight - controlWidth, start.Y + (rowHeight - controlHeight) * 0.5f));
        control();

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + rowHeight));
        ImGui.Dummy(new Vector2(0f, Theme.S(2f)));
    }

    /// <summary>Text pushed to the right edge of the space available (a table cell, usually).</summary>
    public static void TextRight(string text, Vector4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        var available = ImGui.GetContentRegionAvail().X;
        if (available > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - width);
        Text(text, color);
    }

    /// <summary>A sidebar entry: icon and label, lit in gold with a marker when it is the current page.</summary>
    public static bool SidebarTab(string label, FontAwesomeIcon icon, bool selected, bool attention = false)
    {
        var height = Theme.S(40f);
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        var clicked = Press("##tab" + label, new Vector2(width, height), out var hovered, out _);

        if (selected)
        {
            drawList.AddRectFilled(origin, end, Theme.U32(Theme.Mix(Theme.Deep, Theme.Gold, 0.13f)), Theme.S(8f));
            drawList.AddRectFilled(origin + new Vector2(0, Theme.S(9f)), new Vector2(origin.X + Theme.S(3f), end.Y - Theme.S(9f)), Theme.U32(Theme.Gold), Theme.S(2f));
        }
        else if (hovered)
        {
            drawList.AddRectFilled(origin, end, Theme.U32(Theme.CardRaised), Theme.S(8f));
        }

        var iconSize = IconSize(icon);
        var iconSlot = Theme.S(22f);
        var x = origin.X + Theme.S(14f);
        var color = selected ? Theme.Gold : hovered ? Theme.Text : Theme.TextSoft;
        IconAt(drawList, new Vector2(x + (iconSlot - iconSize.X) * 0.5f, origin.Y + (height - iconSize.Y) * 0.5f), icon, color);

        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(new Vector2(x + iconSlot + Theme.S(10f), origin.Y + (height - labelSize.Y) * 0.5f),
            Theme.U32(selected ? Theme.Text : color), label);

        if (attention)
            drawList.AddCircleFilled(new Vector2(end.X - Theme.S(14f), origin.Y + height * 0.5f), Theme.S(3.5f), Theme.U32(Theme.Teal));

        return clicked;
    }

    /// <summary>
    /// Reserves a clickable area at the cursor. Being a real ImGui button, it takes the mouse press for itself, so
    /// pressing a hand-drawn control never drags the window underneath, and it is not clickable through a popup.
    /// </summary>
    private static bool Press(string id, Vector2 size, out bool hovered, out bool held, bool enabled = true)
    {
        size = new Vector2(System.MathF.Max(1f, size.X), System.MathF.Max(1f, size.Y));
        var clicked = ImGui.InvisibleButton(id, size);
        hovered = enabled && ImGui.IsItemHovered();
        held = enabled && ImGui.IsItemActive();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return enabled && clicked;
    }

    public static Vector2 PillSize(string label, UiAssets assets, bool dot = true)
    {
        var text = Measure(label, assets.Small);
        return new Vector2(text.X + Theme.S(dot ? 30f : 20f), Theme.S(22f));
    }

    /// <summary>A small rounded status chip, drawn at an exact position.</summary>
    public static void PillAt(ImDrawListPtr drawList, Vector2 origin, string label, Vector4 color, UiAssets assets, bool dot = true)
    {
        var size = PillSize(label, assets, dot);
        var end = origin + size;
        drawList.AddRectFilled(origin, end, Theme.U32(Theme.Mix(Theme.Card, color, 0.17f)), size.Y * 0.5f);
        drawList.AddRect(origin, end, Theme.U32(Theme.Alpha(color, 0.50f)), size.Y * 0.5f);

        var textX = origin.X + Theme.S(10f);
        if (dot)
        {
            drawList.AddCircleFilled(new Vector2(origin.X + Theme.S(11f), origin.Y + size.Y * 0.5f), Theme.S(3f), Theme.U32(color));
            textX = origin.X + Theme.S(20f);
        }

        var text = Measure(label, assets.Small);
        TextAt(drawList, new Vector2(textX, origin.Y + (size.Y - text.Y) * 0.5f), label, color, assets.Small);
    }

    public static void Pill(string label, Vector4 color, UiAssets assets, bool dot = true)
    {
        PillAt(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), label, color, assets, dot);
        ImGui.Dummy(PillSize(label, assets, dot));
    }

    /// <summary>A pill that can be switched on and off, for filters. Returns true when clicked.</summary>
    public static bool FilterPill(string label, Vector4 color, bool active, UiAssets assets, string? id = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = PillSize(label, assets, dot: false) + new Vector2(Theme.S(6f), Theme.S(4f));
        var end = origin + size;
        var drawList = ImGui.GetWindowDrawList();
        var clicked = Press("##pill" + (id ?? label), size, out var hovered, out _);

        var fill = active ? Theme.Mix(Theme.Card, color, hovered ? 0.30f : 0.22f) : hovered ? Theme.CardRaised : Theme.Card;
        drawList.AddRectFilled(origin, end, Theme.U32(fill), size.Y * 0.5f);
        drawList.AddRect(origin, end, Theme.U32(active ? Theme.Alpha(color, 0.60f) : Theme.BorderSoft), size.Y * 0.5f);

        var text = Measure(label, assets.Small);
        TextAt(drawList, origin + (size - text) * 0.5f, label, active ? color : Theme.TextDim, assets.Small);

        return clicked;
    }

    /// <summary>An on/off switch. Returns true when it was flipped.</summary>
    public static bool Toggle(string id, ref bool value)
    {
        var trackWidth = Theme.S(42f);
        var trackHeight = Theme.S(22f);
        var radius = (trackHeight - Theme.S(6f)) * 0.5f;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(trackWidth, trackHeight);
        var drawList = ImGui.GetWindowDrawList();
        var clicked = Press("##toggle" + id, new Vector2(trackWidth, trackHeight), out var hovered, out _);

        var track = value
            ? Theme.Mix(Theme.TealDeep, Theme.Teal, hovered ? 0.55f : 0.35f)
            : hovered ? Theme.CardHover : Theme.CardRaised;
        drawList.AddRectFilled(origin, end, Theme.U32(track), trackHeight * 0.5f);
        drawList.AddRect(origin, end, Theme.U32(value ? Theme.Alpha(Theme.Teal, 0.8f) : Theme.Border), trackHeight * 0.5f);

        var knobX = value ? end.X - radius - Theme.S(3f) : origin.X + radius + Theme.S(3f);
        drawList.AddCircleFilled(new Vector2(knobX, origin.Y + trackHeight * 0.5f), radius, Theme.U32(value ? Theme.Text : Theme.TextSoft));

        if (!clicked)
            return false;

        value = !value;
        return true;
    }

    public enum ButtonKind
    {
        /// <summary>The one main action in a view: solid teal.</summary>
        Primary,

        /// <summary>Everything else: quiet outline.</summary>
        Secondary,

        /// <summary>Something that removes or replaces data.</summary>
        Danger,
    }

    /// <summary>A button with an optional icon. Returns true when clicked.</summary>
    public static bool Button(string label, FontAwesomeIcon? icon = null, ButtonKind kind = ButtonKind.Secondary, bool enabled = true)
    {
        var text = ImGui.CalcTextSize(label);
        var iconSize = icon is { } i ? IconSize(i) : Vector2.Zero;
        var gap = icon is null ? 0f : Theme.S(8f);
        var size = new Vector2(text.X + iconSize.X + gap + Theme.S(26f), Theme.S(32f));
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var drawList = ImGui.GetWindowDrawList();
        var clicked = Press("##button" + label, size, out var hovered, out var held, enabled);

        Vector4 fill, outline, ink;
        switch (kind)
        {
            case ButtonKind.Primary:
                fill = held ? Theme.Mix(Theme.Teal, Theme.Text, 0.15f) : hovered ? Theme.Mix(Theme.Teal, Theme.Text, 0.08f) : Theme.Teal;
                outline = fill;
                ink = new Vector4(0.02f, 0.09f, 0.12f, 1f);
                break;
            case ButtonKind.Danger:
                fill = hovered ? Theme.Mix(Theme.Card, Theme.Rose, held ? 0.40f : 0.26f) : Theme.Mix(Theme.Card, Theme.Rose, 0.12f);
                outline = Theme.Alpha(Theme.Rose, 0.6f);
                ink = Theme.Rose;
                break;
            default:
                fill = held ? Theme.CardHover : hovered ? Theme.CardRaised : Theme.Card;
                outline = hovered ? Theme.Alpha(Theme.Teal, 0.6f) : Theme.Border;
                ink = hovered ? Theme.Text : Theme.TextSoft;
                break;
        }

        if (!enabled)
        {
            fill = Theme.Alpha(fill, fill.W * 0.35f);
            outline = Theme.Alpha(outline, 0.3f);
            ink = Theme.Alpha(ink, 0.45f);
        }

        drawList.AddRectFilled(origin, end, Theme.U32(fill), Theme.S(7f));
        drawList.AddRect(origin, end, Theme.U32(outline), Theme.S(7f));

        var x = origin.X + Theme.S(13f);
        if (icon is { } glyph)
        {
            IconAt(drawList, new Vector2(x, origin.Y + (size.Y - iconSize.Y) * 0.5f), glyph, ink);
            x += iconSize.X + gap;
        }

        drawList.AddText(new Vector2(x, origin.Y + (size.Y - text.Y) * 0.5f), Theme.U32(ink), label);

        return clicked;
    }

    /// <summary>
    /// One headline number: a dim label, the figure in large type, and a line of context beneath.
    /// </summary>
    public static void StatTile(string label, string value, string? detail, Vector4 accent, Vector2 size, UiAssets assets, string? tooltip = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var drawList = ImGui.GetWindowDrawList();
        var rounding = Theme.S(9f);

        drawList.AddRectFilled(origin, end, Theme.U32(Theme.Card), rounding);
        // A soft wash of the accent colour falling from the top edge.
        drawList.AddRectFilledMultiColor(
            origin + new Vector2(rounding, 1f), new Vector2(end.X - rounding, origin.Y + size.Y * 0.55f),
            Theme.U32(Theme.Alpha(accent, 0.10f)), Theme.U32(Theme.Alpha(accent, 0.10f)),
            Theme.U32(Theme.Alpha(accent, 0f)), Theme.U32(Theme.Alpha(accent, 0f)));
        drawList.AddRect(origin, end, Theme.U32(Theme.BorderSoft), rounding);
        drawList.AddRectFilled(origin + new Vector2(Theme.S(14f), 0), new Vector2(origin.X + Theme.S(46f), origin.Y + Theme.S(2.5f)), Theme.U32(accent), Theme.S(2f));

        var padX = Theme.S(14f);
        TextAt(drawList, origin + new Vector2(padX, Theme.S(13f)), label.ToUpperInvariant(), Theme.TextDim, assets.Small);

        // Fall back to a smaller face when the figure would not fit the tile.
        var font = assets.Figure;
        var valueSize = Measure(value, font);
        if (valueSize.X > size.X - padX * 2)
        {
            font = assets.Title;
            valueSize = Measure(value, font);
            if (valueSize.X > size.X - padX * 2)
            {
                font = assets.Heading;
                valueSize = Measure(value, font);
            }
        }

        var detailHeight = string.IsNullOrEmpty(detail) ? 0f : Measure(detail, assets.Small).Y + Theme.S(4f);
        var valueY = end.Y - Theme.S(12f) - detailHeight - valueSize.Y;
        TextAt(drawList, new Vector2(origin.X + padX, valueY), value, Theme.Text, font);

        if (!string.IsNullOrEmpty(detail))
            TextAt(drawList, new Vector2(origin.X + padX, end.Y - Theme.S(12f) - detailHeight + Theme.S(4f)), detail, Theme.TextDim, assets.Small);

        ImGui.Dummy(size);
        if (tooltip is not null)
            Tooltip(tooltip);
    }
}
