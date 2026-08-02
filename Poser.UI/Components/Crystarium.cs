using System;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// What is left of the vocabulary once controls became their own props: the
/// theme, the stateful-component mount, and the handful of compositions that
/// are pages rather than controls. Every declaration is one prop-bag whose
/// implicit conversion writes ONE element record into the ambient frame arena,
/// so a build pass is a sequence of struct writes into pooled storage.
/// </summary>
public static partial class Crystarium
{
    // Theme survives severance: the imperative controls and the retained
    // vocabulary read ONE token value, so theme access keeps its natural name
    // on both sides and no product call site changes as the legacy surface is
    // retired.
    public static Theme ActiveTheme => LegacyCrystarium.ActiveTheme;

    public static void UseTheme(Theme theme)
    {
        LegacyCrystarium.UseTheme(theme);
        // Sheets are a projection of tokens, so a theme swap invalidates the
        // whole table rather than patching entries.
        ThemeStyles.Invalidate();
    }

    /// <summary>
    /// Mounts (or re-finds) a stateful component and inlines its subtree. The
    /// scope is matched by parent scope, component type and key; the instance
    /// is constructed exactly once, on cold mount. The returned record carries
    /// the scope id, so the id path below it — and therefore every ImGui
    /// identity it owns — follows the component, not its position.
    /// </summary>
    /// <remarks>
    /// A stateful scope is matched by key, so an UNKEYED one is matched by
    /// position and would hand a reordered list its neighbour's state. The
    /// requirement is enforced where the mount happens rather than left to
    /// review.
    ///
    /// The entry stays INTERNAL: a component is mounted through its OWN static
    /// factory, so the three type arguments never appear at a call site.
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

    /// <summary>One full-width band of empty vertical flow.</summary>
    internal static UiNode Spacer(float logicalHeight) => new Row
    {
        Style = new()
        {
            Layout = new()
            {
                Width = UiDim.Fill,
                Height = UiDim.Fixed(logicalHeight),
            },
        },
    };
}
