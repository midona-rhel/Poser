using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Norvrandt is Crystarium's internal element renderer. It owns the
/// stylesheet cascade and layout engine. Widgets (buttons, sliders, etc.)
/// live in the <c>Crystarium</c> library on top of this.
///
/// <code>
///   Norvrandt.Element(new() { Classes = Cls.Row }, () =&gt; {
///       Norvrandt.Element(new() { Style = new() { Width = Sizing.Fill } }, () =&gt; {
///           // raw ImGui content here
///       });
///   });
///
///   Norvrandt.Sheet.Define(Cls.Btn + Cls.Primary, new ButtonStyle { BackgroundColor = MyAccent });
/// </code>
/// </summary>
internal static class Norvrandt
{
    /// <summary>Render a generic element with optional children.</summary>
    public static void Element(ElementProps props, Action? children = null)
        => UI.Element.Render(props, children);

    /// <summary>Low-level: paint chrome at an explicit screen rect with no cursor manipulation.</summary>
    public static void Box(Vector2 min, Vector2 max, in BoxStyle style)
        => BoxRenderer.Draw(ImGui.GetWindowDrawList(), min, max, style);

    /// <summary>Resolved inner width of the enclosing element (or full content-region width if no element is active).</summary>
    public static float AvailableWidth => UI.Element._ambientWidth > 0f ? UI.Element._ambientWidth : ImGui.GetContentRegionAvail().X;

    /// <summary>Resolved inner height of the enclosing element (0 if not inside a sized container).</summary>
    public static float AvailableHeight => UI.Element._ambientHeight;

    /// <summary>True when the current draw is inside a flex row's children lambda.</summary>
    public static bool IsInRow => UI.Element.IsInRow;

    /// <summary>
    /// Register a deferred row child from a widget that manages its own ImGui cursor —
    /// e.g. <see cref="Crystarium.Text"/>. The render lambda is invoked once the row
    /// container resolves flex sizes, with (width, height) of the assigned cell.
    /// </summary>
    public static void RegisterRowItem(Sizing width, Sizing? height, AlignSelf? align, Action<float, float> render)
        => UI.Element.RegisterRowItem(width, height, align, render);

    /// <summary>Stylesheet façade. Define rules with typed selectors and pseudo-classes.</summary>
    public static class Sheet
    {
        // Class-based, untyped style (ElementStyle works for any element)
        public static void Define(StyleClass cls, ElementStyle style) => Stylesheet.Define(cls, style);
        public static void Define(StyleClass cls, PseudoState pseudo, ElementStyle style) => Stylesheet.Define(cls, pseudo, style);
        public static void Define(StyleClassSet classes, ElementStyle style) => Stylesheet.Define(classes, style);
        public static void Define(StyleClassSet classes, PseudoState pseudo, ElementStyle style) => Stylesheet.Define(classes, pseudo, style);

        // Tag-typed sugar (compile-time prevents nonsense fields)
        public static void Define(StyleClass cls, ButtonStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, ButtonStyle s) => Stylesheet.Define(cls, p, s);
        public static void Define(StyleClassSet cls, ButtonStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClassSet cls, PseudoState p, ButtonStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, CheckboxStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, CheckboxStyle s) => Stylesheet.Define(cls, p, s);
        public static void Define(StyleClassSet cls, CheckboxStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClassSet cls, PseudoState p, CheckboxStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, ToggleStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, ToggleStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, IconToggleStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, IconToggleStyle s) => Stylesheet.Define(cls, p, s);


        public static void Define(StyleClass cls, DropdownStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, DropdownStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, TextInputStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, TextInputStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, SliderStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, SliderStyle s) => Stylesheet.Define(cls, p, s);

        public static void Define(StyleClass cls, TextStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, TextStyle s) => Stylesheet.Define(cls, p, s);

        // Raw string selector for advanced/user-defined cases (parses ".btn:hover.primary")
        public static void Define(string selector, ElementStyle style) => Stylesheet.Define(selector, style);

        public static void Reset() => Stylesheet.Reset();
    }
}
