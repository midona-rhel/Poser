using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// THE WINDOW FRAME, and there is one. Every floating Poser window is this
/// chassis told different slots: the title bar with its close affordance and
/// full-width bottom rule, an optional band under it, an optional left rail
/// whose 1px rule bridges the rotated-H, the body that rule opens onto, and the
/// footer band — ModalFooter chrome, full-width top rule, and the same
/// Left/Fill/Right clusters an <see cref="ActionBar"/> has.
///
/// <para>THE ROTATED-H IS THE GEOMETRY (user 2026-08-02): full-width rules
/// above AND below the body, with the rail's vertical rule bridging them. It is
/// stated HERE, once, and no surface restates it.</para>
///
/// <para>THE BODY IS A SLOT, not a container: a surface may flow its content
/// straight into the chassis (the file surface's explorer and preview) or leave
/// the slot empty and host its page in the shared scroll seam over the gap the
/// rail leaves (the Settings page, unchanged). Both are the same frame; the
/// difference is only who owns the scrolling.</para>
/// </summary>
public readonly record struct WindowChassis
{
    public required string Title { get; init; }

    public UiHandler OnClose { get; init; }

    public string? CloseHelp { get; init; }

    /// <summary>An optional band between the title bar and the body — the file
    /// surface's navigation row. One node, so a surface that wants two states a
    /// column of two.</summary>
    public UiNode Band { get; init; }

    /// <summary>The rail's CONTENT. An empty rail is no rail and no rule: the
    /// body then spans the whole frame.</summary>
    public UiChildren Rail { get; init; }

    /// <summary>The rail's width, RULE INCLUDED; 0 takes the Settings rail's
    /// own.</summary>
    public float RailWidth { get; init; }

    /// <summary>What flows right of the rail rule. None leaves the gap for a
    /// caller-hosted root — see the type remarks.</summary>
    public UiNode Body { get; init; }

    /// <inheritdoc cref="ActionBar.Left"/>
    public UiChildren FooterLeft { get; init; }

    /// <inheritdoc cref="ActionBar.Fill"/>
    public UiChildren FooterFill { get; init; }

    /// <inheritdoc cref="ActionBar.Right"/>
    public UiChildren FooterRight { get; init; }

    /// <summary>
    /// The window's own paint and its tree, in the one order they may happen:
    /// the glass chrome sits behind everything the root draws, and naming
    /// <c>DrawChrome</c> is the chassis' business rather than every window's.
    /// </summary>
    public static void Render<TProps>(
        UiRoot root,
        Vector2 origin,
        Vector2 size,
        in TProps props,
        UiBuilder<TProps> build)
    {
        // STATED, not defaulted: a floating window is the full glass recipe —
        // backdrop blur behind it and the panel shadow around it — which is
        // exactly what the accepted Settings window draws, and the pair is now
        // a choice the chrome offers rather than a constant.
        LegacyCrystarium.FloatingSurface.DrawChrome(
            ImGui.GetWindowDrawList(),
            origin,
            origin + size,
            Crystarium.ActiveTheme.Radii.Window,
            shadow: true,
            blur: true);
        root.Render(origin, size, in props, build);
    }

    public static implicit operator UiNode(WindowChassis chassis) =>
        chassis.Emit();

    private UiNode Emit()
    {
        float railWidth = RailWidth > 0f
            ? RailWidth
            : Crystarium.ActiveTheme.Settings.NavigationWidth;
        bool hasRail = Rail.Count != 0;

        return new Column
        {
            Style = new()
            {
                Layout = new() { Width = UiDim.Fill, Height = UiDim.Fill },
            },
            Children =
            [
                new ActionBar
                {
                    Left = ActionBar.Title(Title),
                    Right = new IconAction
                    {
                        Icon = TablerIcon.X,
                        OnClick = OnClose,
                        Help = CloseHelp,
                    },
                    Separator = ActionBarSeparator.Bottom,
                    Key = "header",
                },
                Band,
                new Row
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Width = UiDim.Fill,
                            Height = UiDim.Fill,
                        },
                    },
                    Children =
                    [
                        !hasRail ? UiNode.None : (UiNode)new Column
                        {
                            Sheet = SheetFamily.NavRail,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fixed(railWidth - 1f),
                                },
                            },
                            Children = Rail,
                        },
                        // The rail/page rule, flowed as the rail's last pixel:
                        // it is the H's bridge, and it belongs to the rail, so
                        // a frame without a rail has none.
                        !hasRail ? UiNode.None : (UiNode)new Element
                        {
                            Sheet = SheetFamily.BarRule,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fixed(1f),
                                    Height = UiDim.Fill,
                                },
                            },
                        },
                        Body,
                    ],
                },
                new ActionBar
                {
                    Left = FooterLeft,
                    Fill = FooterFill,
                    Right = FooterRight,
                    Separator = ActionBarSeparator.Top,
                    FooterChrome = true,
                    Key = "footer",
                },
            ],
        };
    }
}
