using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The main content, popped out and FROZEN to one actor: its own inspector
/// and graphical-map instances (so its gestures and edit sessions never share
/// an edge with the main window's), rendered under a selection scope that
/// substitutes the frozen entity for the live selection while — and only
/// while — this window draws. It never retargets; it lives until the actor
/// leaves the scene or the window is closed. The bar is BARE: title,
/// collapse, close — no toolbar controls in any state.
/// </summary>
public sealed class PopOutWindow : Window
{
    private const float MinContentWidth = 620f;
    private const float MinContentHeight = 420f;

    /// <summary>The bar is the modal bar every floating frame wears.</summary>
    private static float HeaderHeight =>
        Crystarium.ActiveTheme.Floating.ModalBarHeight;

    private static readonly string[] TabLabels =
        ["Pose", "Animation", "Appearance"];

    private static int _nextIdentity;

    private readonly MainWindow _main;
    private readonly SceneSession _scene;
    private readonly SelectionSession _selection;
    private readonly IGPoseService _gPose;
    private readonly PoseInspectorPane _inspector;
    private readonly GraphicalBonePane _graphical;
    private readonly AnimationPane _animationPane;
    private readonly AppearancePane _appearancePane;
    private readonly Game.Animation.AnimationCatalogLoader _animationCatalog;
    private readonly Application.Animation.AnimationSession _animation;
    private readonly SelectionScope _scope;
    private readonly Guid _lineage;
    private readonly string _ownerId;
    private readonly int _identity;
    private int _tab;
    private bool _disposed;
    private bool _collapsed;
    private float _savedHeight = 620f;
    private float _lastWidth = 760f;
    private bool _restorePending;

    /// <summary>The window is gone — closed by hand or by its actor's
    /// destruction — and the window set should forget it.</summary>
    public event Action<PopOutWindow>? OnDismissed;

    private PopOutWindow(
        IServiceProvider services,
        MainWindow main,
        ActorId actor,
        int identity)
        : base($"###poser_popout_{identity}",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        _main = main;
        _identity = identity;
        _ownerId = $"poser-popout-{identity}";
        _scene = services.GetRequiredService<SceneSession>();
        _selection = _scene.Selection;
        _gPose = services.GetRequiredService<Services.IGPoseService>();
        // This window's OWN animation/appearance panes, for the same reason
        // as the inspector pair below: the DI singletons are the main
        // window's live-selection instances, and sharing them would
        // alternate each pane's subject (picker state, in-flight edits,
        // readout throttles) between two windows every frame.
        _animationPane = ActivatorUtilities
            .CreateInstance<AnimationPane>(services);
        _appearancePane = ActivatorUtilities
            .CreateInstance<AppearancePane>(services);
        _animationCatalog = services
            .GetRequiredService<Game.Animation.AnimationCatalogLoader>();
        _animation = services
            .GetRequiredService<Application.Animation.AnimationSession>();

        // This window's OWN inspector/map pair: a frozen subject must not
        // share gesture or edit-session state with the live window's panes —
        // an alternating subject would cancel a drag on every frame.
        var graphical = ActivatorUtilities
            .CreateInstance<GraphicalBonePane>(services);
        _graphical = graphical;
        _inspector = ActivatorUtilities
            .CreateInstance<PoseInspectorPane>(services);
        _inspector.DrawMapInline = graphical.DrawInline;
        // This window's OWN animation pane owns the expression row and the
        // picker it opens, for the same reason the panes above are per-window.
        _inspector.DrawExpressionRow = _animationPane.DrawExpressionRow;
        graphical.SidesSwapped =
            Config.ConfigurationService.Instance.Config.UI.MapMirrorSelection;
        _inspector.GetMapMirror = () => graphical.SidesSwapped;
        _inspector.SetMapMirror = on =>
        {
            graphical.SidesSwapped = on;
            Config.ConfigurationService.Instance
                .Config.UI.MapMirrorSelection = on;
            Config.ConfigurationService.Instance.Save();
        };
        _inspector.DescriptorDisplayName = MainWindow.ActorDisplayName;
        var bindings = services
            .GetRequiredService<Game.Bindings.StableBindingRegistry>();
        _inspector.ActorDisplayNameProvider = legacyActor =>
            bindings.GetActorId(legacyActor) is { } displayId
                ? Config.ConfigurationService.Instance.GetDisplayName(
                    displayId.LogicalId,
                    MainWindow.DisplayName(legacyActor.Name))
                : MainWindow.DisplayName(legacyActor.Name);

        _lineage = actor.LogicalId;
        _scope = new SelectionScope(SelectionId.ForActor(actor));
        _selection.TrackScope(_scope);

        Size = new Vector2(760f, 620f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public static PopOutWindow Create(
        IServiceProvider services, MainWindow main, ActorId actor) =>
        new(services, main, actor, _nextIdentity++);

    public override void PreDraw()
    {
        base.PreDraw();
        float header = HeaderHeight + 2f;
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(MinContentWidth, header),
                MaximumSize = new Vector2(float.MaxValue, header),
            }
            : new WindowSizeConstraints
            {
                MinimumSize =
                    new Vector2(MinContentWidth, MinContentHeight),
                MaximumSize =
                    new Vector2(float.MaxValue, float.MaxValue),
            };
        if (_collapsed)
        {
            Size = new Vector2(_lastWidth, header);
            SizeCondition = ImGuiCond.Always;
        }
        else if (_restorePending)
        {
            Size = new Vector2(_lastWidth, _savedHeight);
            SizeCondition = ImGuiCond.Always;
            _restorePending = false;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Crystarium.ActiveTheme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Crystarium.ActiveTheme.TextDim);
        ImGui.PushStyleColor(ImGuiCol.Border, Crystarium.ActiveTheme.Border);
        ImGui.PushStyleColor(ImGuiCol.Button, Crystarium.ActiveTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Crystarium.ActiveTheme.SurfaceSunken);
        ImGui.PushStyleColor(ImGuiCol.Header, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
        base.PostDraw();
    }

    public override void Draw()
    {
        // The frozen actor is this window's whole reason: gone means closed,
        // and it cannot retarget by design.
        if (FrozenActor() is not { } actor || !_gPose.IsGPosing)
        {
            IsOpen = false;
            return;
        }

        float s = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        _lastWidth = size.X / s;
        var max = min + size;
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            _ownerId, InteractionLayer.Window, min, max);
        try
        {
            // The file dialog's chassis, verbatim (user: every floating
            // window wears the same glass).
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, Crystarium.ActiveTheme.Radii.Window);
            float headerBottom = DrawHeader(actor, min, max, s, dl);
            if (!_collapsed)
                DrawBody(
                    actor, new Vector2(min.X, headerBottom), max, s);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }

        // The shell's rule, for the shell's reason (MainWindow.Draw): the
        // Appearance dialogs are pumped at WINDOW level, unconditionally,
        // outside the owner scope — so a dialog opened from the Appearance
        // tab survives collapsing the window or switching tabs under it.
        // This window mints its own AppearancePane, so nothing else pumps
        // it: without this line the pop-out's MCDF Import/Export are dead
        // buttons, because those dialogs draw from DrawBrowsers alone.
        _appearancePane.DrawBrowsers();
        // Same rule for the expression row's picker: the row is drawn on this
        // window's inspector, on any tab, so its surface is pumped here. A
        // no-op on frames the animation tab already drew it.
        _animationPane.DrawExpressionPicker();
    }

    public override void OnClose()
    {
        base.OnClose();
        _selection.ForgetScope(_scope);
        // This window privately minted its map pane (ActivatorUtilities, not
        // DI), so this window must dispose it — its decoded bone-map
        // textures leaked once per open/close cycle otherwise. Exactly once:
        // close is the window's end of life, it never reopens.
        if (!_disposed)
        {
            _disposed = true;
            _graphical.Dispose();
        }
        OnDismissed?.Invoke(this);
    }

    private ActorDescriptor? FrozenActor()
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == _lineage)
                return actor;
        return null;
    }

    /// <summary>Bare by design: the title, the collapse chevron and the
    /// close X. No gizmo controls in any state (user).</summary>
    private float DrawHeader(
        in ActorDescriptor actor, Vector2 min, Vector2 max, float s,
        ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = HeaderHeight * s;
        // The title stands on the content column's inset — one aligned left
        // edge with the tab strip and the pane below it.
        float inset = theme.Page.Inset * s;
        float side = theme.Floating.CloseActionSize;
        float step = (side + theme.Page.ActionGap) * s;

        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f - step * 2f, height),
            MainWindow.ActorDisplayName(actor),
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Color = theme.Chrome.Text,
            });

        float y = min.Y + (height - side * s) * 0.5f;
        float x = max.X - theme.Floating.CloseInset * s - side * s;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        Crystarium.IconButton(
            "x",
            () => IsOpen = false,
            ControlStyle.Square(side),
            help: "Close this window",
            id: $"##popout-close-{_identity}");
        x -= step;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        Crystarium.IconButton(
            _collapsed ? "chevron-down" : "chevron-up",
            () =>
            {
                if (!_collapsed)
                    _savedHeight =
                        ImGui.GetWindowSize().Y / ImGuiHelpers.GlobalScale;
                else
                    _restorePending = true;
                _collapsed = !_collapsed;
            },
            ControlStyle.Square(side),
            help: _collapsed ? "Expand the window" : "Collapse to the bar",
            id: $"##popout-collapse-{_identity}");

        if (_collapsed)
            return min.Y + height;
        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return min.Y + height;
    }

    private void DrawBody(
        in ActorDescriptor actor, Vector2 min, Vector2 max, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * s;
        float barHeight = AppShellView.ToolbarHeight * s;
        var tabSize = Crystarium.MeasureSegmentedControl(TabLabels);
        ImGui.SetCursorScreenPos(new Vector2(
            min.X + inset,
            min.Y + (barHeight - tabSize.Y) * 0.5f));
        Crystarium.SegmentedControl(
            $"##popout-tabs-{_identity}",
            TabLabels,
            _tab,
            chosen => _tab = chosen,
            alignFirstTabToCursor: true);

        // The same right cluster the shell's workspace bar wears — animation
        // and physics — acting on THIS window's frozen actor through its
        // CURRENT id (the creation-time id goes stale across redraws, and a
        // stale id read as "no override" would freeze the switches).
        var frozen = actor.Id;
        bool animationAvailable = _animation.IsSupported(frozen);
        bool animationOn =
            _animation.OverridesFor(frozen).OverallSpeed is not 0f;
        // Physics is one PROCESS-GLOBAL patch: the switch shows the global
        // state (the tooltip already says "whole scene"), while the toggle
        // still books the request against this window's actor.
        bool physicsOn = !_animation.IsPhysicsFrozen;
        Crystarium.ActionBar(
            $"popout-actions-{_identity}",
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, barHeight),
            static _ => { },
            right =>
            {
                right.Switch(
                    "Animation",
                    animationOn,
                    next =>
                    {
                        if (next) _animation.ClearSpeed(frozen);
                        else _animation.SetSpeed(frozen, 0f);
                    },
                    animationOn
                        ? "Switch off to pause this actor's animation"
                        : "Switch on to resume this actor's animation",
                    disabled: !animationAvailable);
                right.Switch(
                    "Physics",
                    physicsOn,
                    next => _animation.SetPhysicsFrozen(frozen, !next),
                    physicsOn
                        ? "Switch off to freeze physics for the whole scene"
                        : "Switch on to resume physics for the whole scene",
                    disabled: !animationAvailable);
            },
            ActionBarSeparator.None);

        // No extra bottom inset: the hosted pane owns its own footer band,
        // exactly as it does inside the main window (user 2026-08-11: the
        // pop-out's footer read wider than the shell's).
        var contentOrigin = new Vector2(min.X + inset, min.Y + barHeight);
        var contentSize = new Vector2(
            MathF.Max(1f, max.X - min.X - inset * 2f),
            MathF.Max(1f, max.Y - contentOrigin.Y));

        // THE substitution: while this window draws its content, the frozen
        // scope IS the selection — every pane and facade below resolves and
        // edits this actor, and the live selection never notices.
        using (_selection.BeginScope(_scope))
        {
            _inspector.SetSelection(_scope.Primary);
            switch (_tab)
            {
                case 1:
                    _animationCatalog.EnsureLoaded();
                    _animationPane.Draw(contentOrigin, contentSize);
                    break;
                case 2:
                    _appearancePane.Draw(contentOrigin, contentSize);
                    break;
                default:
                    _inspector.Draw(contentOrigin, contentSize);
                    break;
            }
        }
    }
}
