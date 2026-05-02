using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// HTML-shaped UI primitive. The single entry point for the Crystarium element system.
///
/// <code>
///   Crystarium.Button("Save");
///   Crystarium.Button("Save", Save);                          // with onClick
///   Crystarium.Button("Save", Cls.Btn + Cls.Primary, Save);   // with classes
///
///   Crystarium.Element(new() { Classes = Cls.Row }, () => {
///       Crystarium.Text("Color", Cls.Label);
///       Crystarium.Element(new() { Style = new() { Width = Sizing.Fill } }, () => {
///           // raw ImGui content here
///       });
///   });
///
///   Crystarium.Sheet.Define(Cls.Btn + Cls.Primary, new ButtonStyle { BackgroundColor = MyAccent });
///   Crystarium.Sheet.Define(Cls.Btn, PseudoState.Hover, new ButtonStyle { ... });
/// </code>
/// </summary>
public static partial class Crystarium
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

        public static void Define(StyleClass cls, ScrubberStyle s) => Stylesheet.Define(cls, s);
        public static void Define(StyleClass cls, PseudoState p, ScrubberStyle s) => Stylesheet.Define(cls, p, s);

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
