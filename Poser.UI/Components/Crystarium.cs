using System.Numerics;

namespace Poser.UI.Reactive;

/// <summary>
/// The declarative vocabulary. Every factory writes ONE element record into
/// the ambient frame arena and hands back an opaque handle, so a build pass
/// is a sequence of struct writes into pooled storage — no tree of objects
/// is ever constructed. Nothing here paints: painting is the root's single
/// walk, after layout has resolved every box.
/// </summary>
public static partial class Crystarium
{
    public static UiNode Row(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Row, in sx, children, key);

    public static UiNode Column(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Column, in sx, children, key);

    public static UiNode Stack(UiStyle sx = default, UiChildren children = default, UiKey key = default) =>
        Box(UiFlow.Stack, in sx, children, key);

    /// <summary>A text run at its intrinsic size. Unset size and color
    /// resolve from the active theme inside the renderer.</summary>
    public static UiNode Text(string text, float? size = null, Vector4? color = null, UiKey key = default)
    {
        ElementRecord record = default;
        record.Kind = ElementKind.Text;
        record.Text = text;
        record.TextSize = size ?? 0f;
        Tint(ref record, color);
        record.Key = key;
        return new UiNode(FrameArena.Require().AddElement(record));
    }

    /// <summary>A Tabler glyph on a fixed logical square. The record stores
    /// the registry NAME, which is a compile-time literal, so declaring an
    /// icon costs no allocation.</summary>
    public static UiNode Svg(
        Poser.UI.TablerIcon icon, float size = 16f, Vector4? color = null, UiKey key = default)
    {
        ElementRecord record = default;
        record.Kind = ElementKind.Svg;
        record.Text = Poser.UI.Tabler.NameFor(icon);
        record.TextSize = size;
        Tint(ref record, color);
        record.Key = key;
        return new UiNode(FrameArena.Require().AddElement(record));
    }

    /// <summary>
    /// Mounts (or re-finds) a stateful component and inlines its subtree.
    /// The scope is matched by parent scope, component type, and key; the
    /// instance is constructed exactly once, on cold mount. The returned
    /// record carries the scope id, so the id path below it — and therefore
    /// every ImGui identity it owns — follows the component, not its
    /// position.
    /// </summary>
    public static UiNode Component<TComponent, TProps, TState>(in TProps props, UiKey key = default)
        where TComponent : StatefulComponent<TProps, TState>, new()
    {
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

    private static UiNode Box(UiFlow flow, in UiStyle sx, UiChildren children, UiKey key)
    {
        ElementRecord record = default;
        record.Kind = ElementKind.Box;
        // The factory NAMES the flow, so it always wins over the patch.
        record.Style = UiStyle.Extend(
            sx,
            new UiStyle(UiStyleFields.Flow, flow, 0f, default, default, default, default, default, default));
        record.Key = key;
        record.ChildStart = children.Start;
        record.ChildCount = children.Count;
        return new UiNode(FrameArena.Require().AddElement(record));
    }

    private static void Tint(ref ElementRecord record, Vector4? color)
    {
        if (color is not { } value)
            return;
        record.TextColor = value;
        record.HasTextColor = true;
    }
}
