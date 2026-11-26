using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace SubLedger.Ui;

/// <summary>
/// SubLedger's look: deep-sea navy surfaces, brass gold for money and emphasis, sea teal for things you can press.
/// The colours are taken from the logo.
/// </summary>
internal static class Theme
{
    // Surfaces, darkest to lightest.
    public static readonly Vector4 Abyss = new(0.024f, 0.043f, 0.071f, 0.985f);
    public static readonly Vector4 Deep = new(0.035f, 0.063f, 0.102f, 1.00f);
    public static readonly Vector4 Card = new(0.055f, 0.094f, 0.145f, 0.96f);
    public static readonly Vector4 CardRaised = new(0.075f, 0.125f, 0.188f, 1.00f);
    public static readonly Vector4 CardHover = new(0.095f, 0.155f, 0.228f, 1.00f);
    public static readonly Vector4 Border = new(0.145f, 0.220f, 0.310f, 1.00f);
    public static readonly Vector4 BorderSoft = new(0.110f, 0.170f, 0.245f, 1.00f);
    public static readonly Vector4 Hairline = new(1f, 1f, 1f, 0.055f);

    // Accents.
    public static readonly Vector4 Gold = new(0.925f, 0.757f, 0.380f, 1.00f);
    public static readonly Vector4 GoldBright = new(1.000f, 0.882f, 0.588f, 1.00f);
    public static readonly Vector4 GoldDeep = new(0.640f, 0.490f, 0.200f, 1.00f);
    public static readonly Vector4 Teal = new(0.255f, 0.800f, 0.820f, 1.00f);
    public static readonly Vector4 TealDeep = new(0.110f, 0.420f, 0.470f, 1.00f);
    public static readonly Vector4 Mint = new(0.440f, 0.860f, 0.620f, 1.00f);
    public static readonly Vector4 Amber = new(0.960f, 0.670f, 0.290f, 1.00f);
    public static readonly Vector4 Rose = new(0.945f, 0.420f, 0.490f, 1.00f);
    public static readonly Vector4 Blue = new(0.430f, 0.670f, 0.980f, 1.00f);
    public static readonly Vector4 Violet = new(0.660f, 0.520f, 0.960f, 1.00f);

    // Text.
    public static readonly Vector4 Text = new(0.930f, 0.950f, 0.970f, 1.00f);
    public static readonly Vector4 TextSoft = new(0.730f, 0.780f, 0.835f, 1.00f);
    public static readonly Vector4 TextDim = new(0.500f, 0.565f, 0.640f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.345f, 0.405f, 0.480f, 1.00f);

    public static readonly Vector4 Clear = new(0f, 0f, 0f, 0f);

    public static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary>Design pixels to screen pixels.</summary>
    public static float S(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 S(float x, float y) => new Vector2(x, y) * ImGuiHelpers.GlobalScale;

    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    public static Vector4 Alpha(Vector4 color, float alpha) => color with { W = alpha };

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, t);

    /// <summary>0..1, rising and falling smoothly over the given period.</summary>
    public static float Pulse(double periodMs = 1800)
    {
        var t = Environment.TickCount64 % periodMs / periodMs;
        return (float)((Math.Sin(t * Math.PI * 2.0) + 1.0) * 0.5);
    }

    private static int pushedColors;
    private static int pushedVars;

    private static void Color(ImGuiCol target, Vector4 color)
    {
        ImGui.PushStyleColor(target, color);
        pushedColors++;
    }

    private static void Var(ImGuiStyleVar target, float value)
    {
        ImGui.PushStyleVar(target, value);
        pushedVars++;
    }

    private static void Var(ImGuiStyleVar target, Vector2 value)
    {
        ImGui.PushStyleVar(target, value);
        pushedVars++;
    }

    /// <summary>Applies the theme to everything drawn until <see cref="PopWindow"/>.</summary>
    public static void PushWindow()
    {
        Color(ImGuiCol.WindowBg, Abyss);
        Color(ImGuiCol.ChildBg, Clear);
        Color(ImGuiCol.PopupBg, new Vector4(0.040f, 0.070f, 0.112f, 0.985f));
        Color(ImGuiCol.Border, Border);
        Color(ImGuiCol.TitleBg, Deep);
        Color(ImGuiCol.TitleBgActive, new Vector4(0.045f, 0.082f, 0.130f, 1f));
        Color(ImGuiCol.TitleBgCollapsed, Deep);
        Color(ImGuiCol.Text, Text);
        Color(ImGuiCol.TextDisabled, TextMuted);
        Color(ImGuiCol.FrameBg, new Vector4(0.040f, 0.072f, 0.115f, 1f));
        Color(ImGuiCol.FrameBgHovered, CardRaised);
        Color(ImGuiCol.FrameBgActive, CardHover);
        Color(ImGuiCol.Button, CardRaised);
        Color(ImGuiCol.ButtonHovered, CardHover);
        Color(ImGuiCol.ButtonActive, Mix(CardHover, Teal, 0.25f));
        Color(ImGuiCol.Header, Mix(Card, Teal, 0.18f));
        Color(ImGuiCol.HeaderHovered, Mix(Card, Teal, 0.28f));
        Color(ImGuiCol.HeaderActive, Mix(Card, Teal, 0.38f));
        Color(ImGuiCol.ScrollbarBg, Clear);
        Color(ImGuiCol.ScrollbarGrab, Border);
        Color(ImGuiCol.ScrollbarGrabHovered, Mix(Border, Teal, 0.35f));
        Color(ImGuiCol.ScrollbarGrabActive, Mix(Border, Teal, 0.55f));
        Color(ImGuiCol.Separator, BorderSoft);
        Color(ImGuiCol.SeparatorHovered, Border);
        Color(ImGuiCol.SeparatorActive, Teal);
        Color(ImGuiCol.CheckMark, Gold);
        Color(ImGuiCol.SliderGrab, Teal);
        Color(ImGuiCol.SliderGrabActive, Gold);
        Color(ImGuiCol.ResizeGrip, Alpha(Border, 0.6f));
        Color(ImGuiCol.ResizeGripHovered, Alpha(Teal, 0.7f));
        Color(ImGuiCol.ResizeGripActive, Gold);
        Color(ImGuiCol.TextSelectedBg, Alpha(Teal, 0.35f));
        Color(ImGuiCol.NavHighlight, Teal);
        Color(ImGuiCol.Tab, Card);
        Color(ImGuiCol.TabHovered, CardHover);
        Color(ImGuiCol.TabActive, CardRaised);
        Color(ImGuiCol.BorderShadow, Clear);

        Var(ImGuiStyleVar.WindowRounding, S(10f));
        Var(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Var(ImGuiStyleVar.WindowBorderSize, 1f);
        Var(ImGuiStyleVar.FrameRounding, S(6f));
        Var(ImGuiStyleVar.FramePadding, S(9f, 7f));
        Var(ImGuiStyleVar.ItemSpacing, S(8f, 7f));
        Var(ImGuiStyleVar.PopupRounding, S(8f));
        Var(ImGuiStyleVar.ScrollbarRounding, S(8f));
        Var(ImGuiStyleVar.ScrollbarSize, S(11f));
    }

    public static void PopWindow()
    {
        ImGui.PopStyleVar(pushedVars);
        ImGui.PopStyleColor(pushedColors);
        pushedVars = 0;
        pushedColors = 0;
    }
}
