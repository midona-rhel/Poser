using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The declarative vocabulary. Every factory writes ONE element record into
/// the ambient frame arena and hands back an opaque handle, so a build pass
/// is a sequence of struct writes into pooled storage — no tree of objects
/// is ever constructed. Nothing here paints: painting is the root's single
/// walk, after layout has resolved every box.
/// </summary>
public static partial class Crystarium
{
    // Theme survives severance: the imperative controls and the retained
    // vocabulary read ONE token value, so theme access keeps its natural
    // name on both sides and no product call site changes as the legacy
    // surface is retired.
    public static Theme ActiveTheme => LegacyCrystarium.ActiveTheme;

    public static void UseTheme(Theme theme) => LegacyCrystarium.UseTheme(theme);

    public static UiNode Row(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Row, in sx, children, key);

    public static UiNode Column(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Column, in sx, children, key);

    public static UiNode Stack(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Stack, in sx, children, key);

    /// <summary>A text run. Unset size and color resolve from the active theme
    /// inside the renderer. <paramref name="overflow"/> is what decides whether
    /// the run is cut — sizing it through <paramref name="sx"/> says how much
    /// room it takes, not that it may not spill, exactly as in CSS.</summary>
    public static UiNode Text(
        string text, float? size = null, Vector4? color = null,
        UiStyle sx = default, TextOverflow overflow = TextOverflow.Visible,
        FontFamily family = default, FontWeight? weight = null,
        UiKey key = default) =>
        TextCore(
            text, size, color, in sx, key, overflow, previewOnClip: false,
            family, weight);

    /// <summary>As above, with the truncation readout a control's own label
    /// wants: a cut run offers its full text while the CONTROL is hovered.
    /// Internal because it is only ever right for a label a control owns —
    /// composed body text answers to its own layout, not to a hit box. The
    /// readout means nothing without <see cref="TextOverflow.Truncate"/>,
    /// which is why the two stay separate arguments.</summary>
    internal static UiNode TextCore(
        string text, float? size, Vector4? color, in UiStyle sx, UiKey key,
        TextOverflow overflow, bool previewOnClip, FontFamily family = default,
        FontWeight? weight = null)
    {
        ElementRecord record = default;
        record.Kind = ElementKind.Text;
        record.Text = text;
        record.TextSize = size ?? 0f;
        record.TextFamily = family;
        // The enum's values are 400/500/600, so the hundreds digit is the whole
        // of it and zero stays free to mean "unstated" — which resolves Regular
        // inside the renderer rather than here.
        record.TextWeight = weight is { } stated ? (byte)((int)stated / 100) : (byte)0;
        record.Style = sx;
        record.TextOverflow = overflow;
        record.TextPreviewOnClip = previewOnClip;
        Tint(ref record, color);
        record.Key = key;
        return FrameArena.Require().AddElement(record);
    }

    /// <summary>A Tabler glyph on a fixed logical square. The record stores
    /// the registry NAME, which is a compile-time literal, so declaring an
    /// icon costs no allocation.</summary>
    public static UiNode Svg(
        Poser.UI.TablerIcon icon, float size = 16f, Vector4? color = null, UiKey key = default) =>
        SvgCore(Poser.UI.Tabler.NameFor(icon), size, color, true, 1f, 0f, key);

    /// <summary>As above, with the glyph's stroke stated in the icon's own
    /// 24-unit viewBox. Small glyphs need a heavier one to read at all, and the
    /// weight belongs to the DECLARATION — it is a property of how the icon is
    /// used, not of the icon.</summary>
    internal static UiNode Svg(
        Poser.UI.TablerIcon icon, float size, Vector4? color, float strokeWidth,
        UiKey key = default) =>
        SvgCore(
            Poser.UI.Tabler.NameFor(icon), size, color, true, 1f, strokeWidth, key);

    /// <summary>
    /// Registry-NAME form, for the glyphs the enum does not carry, plus the two
    /// knobs a control-owned glyph needs: its own opacity, and whether
    /// currentColor reaches it at all. A glyph that opts OUT takes the icon
    /// renderer's default tint, which is what a control wants when its
    /// foreground is a compensated LABEL color the glyph must not borrow.
    /// </summary>
    internal static UiNode Svg(
        string name, float size, bool inheritsColor, float opacity, UiKey key = default) =>
        SvgCore(name, size, null, inheritsColor, opacity, 0f, key);

    private static UiNode SvgCore(
        string name, float size, Vector4? color, bool inheritsColor, float opacity,
        float strokeWidth, UiKey key)
    {
        ElementRecord record = default;
        record.Kind = ElementKind.Svg;
        record.Text = name;
        record.TextSize = size;
        record.SvgInheritsColor = inheritsColor;
        record.SvgOpacity = opacity;
        record.SvgStroke = strokeWidth;
        Tint(ref record, color);
        record.Key = key;
        return FrameArena.Require().AddElement(record);
    }

    /// <summary>
    /// Mounts (or re-finds) a stateful component and inlines its subtree.
    /// The scope is matched by parent scope, component type, and key; the
    /// instance is constructed exactly once, on cold mount. The returned
    /// record carries the scope id, so the id path below it — and therefore
    /// every ImGui identity it owns — follows the component, not its
    /// position.
    /// </summary>
    /// <remarks>
    /// A stateful scope is matched by key, so an UNKEYED one is matched by
    /// position and would hand a reordered list its neighbour's state. The
    /// key is therefore mandatory, and the requirement is enforced where the
    /// mount happens rather than left to review.
    ///
    /// The entry stays INTERNAL: a component is mounted through its OWN
    /// static factory, so the three type arguments never appear at a call
    /// site and the mount shape can change without breaking authors.
    /// </remarks>
    internal static UiNode Component<TComponent, TProps, TState>(in TProps props, UiKey key)
        where TComponent : StatefulComponent<TProps, TState>, new()
    {
        // Unconditional: a release build that silently matched by position
        // would hand a reordered list its neighbour's state, which is a
        // corruption no DEBUG-only guard may be trusted to have caught.
        if (key.Kind == UiKeyKind.None)
            throw new ArgumentException(
                $"stateful components require an explicit stable key ({typeof(TComponent).Name})",
                nameof(key));

        UiRoot root = UiRoot.Require();
        FrameArena arena = FrameArena.Require();
        ScopeTable.Scope scope = root.Scopes.GetOrCreate(
            StatefulComponentBase.Ambient?.Id ?? 0, typeof(TComponent), key, arena.FrameId);
        scope.Instance ??= new TComponent();
        UiNode node = ((TComponent)scope.Instance).MountAndRender(scope, props);
        if (!node.IsNone)
            arena[node.Index].ScopeId = scope.Id;
        return node;
    }

    private static UiNode Box(UiFlow flow, in UiStyle sx, UiChildren children, UiKey key) =>
        BoxCore(flow, in sx, children, key, null, 0, null, 0f);

    /// <summary>
    /// A box that DECORATES: it reserves nothing and takes no input, but the
    /// walk hands its painter the same rect and identity an interactive element
    /// would get, before its children. That covers a bar, a rule, and a
    /// geometric help registration with one shape.
    /// </summary>
    internal static UiNode PaintedBox(
        UiFlow flow,
        in UiStyle sx,
        UiChildren children,
        UiKey key,
        IInteractivePainter painter,
        byte paintArg = 0,
        string? help = null,
        float f2 = 0f) =>
        BoxCore(flow, in sx, children, key, painter, paintArg, help, f2);

    private static UiNode BoxCore(
        UiFlow flow, in UiStyle sx, UiChildren children, UiKey key,
        IInteractivePainter? painter, byte paintArg, string? help, float f2)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(children);
        ElementRecord record = default;
        record.Kind = ElementKind.Box;
        // The factory NAMES the flow, so it always wins over the patch.
        record.Style = UiStyle.Extend(
            sx,
            new UiStyle(UiStyleFields.Flow, flow, 0f, default, default, default, default, default, default));
        record.Key = key;
        record.ChildStart = children.Start;
        record.ChildCount = children.Count;
        record.PainterSlot = painter is null ? 0 : arena.AddObject(painter);
        record.PaintArg = paintArg;
        record.Help = help;
        record.F2 = f2;
        return arena.AddElement(record);
    }

    private static void Tint(ref ElementRecord record, Vector4? color)
    {
        if (color is not { } value)
            return;
        record.TextColor = value;
        record.HasTextColor = true;
    }
}
