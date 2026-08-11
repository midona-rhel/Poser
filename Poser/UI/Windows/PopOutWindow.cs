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
    private const float HeaderHeight = 34f;
    private const float MinContentWidth = 620f;
    private const float MinContentHeight = 420f;

    private static readonly string[] TabLabels =
        ["Pose", "Animation", "Appearance"];

    private static int _nextIdentity;

    private readonly MainWindow _main;
    private readonly SceneSession _scene;
    private readonly SelectionSession _selection;
    private readonly IGPoseService _gPose;
    private readonly PoseInspectorPane _inspector;
    private readonly AnimationPane _animationPane;
    private readonly AppearancePane _appearancePane;
    private readonly Game.Animation.AnimationCatalogLoader _animationCatalog;
    private readonly SelectionScope _scope;
    private readonly Guid _lineage;
    private readonly string _ownerId;
    private readonly int _identity;
    private int _tab;
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
        _animationPane = services.GetRequiredService<AnimationPane>();
        _appearancePane = services.GetRequiredService<AppearancePane>();
        _animationCatalog = services
            .GetRequiredService<Game.Animation.AnimationCatalogLoader>();

        // This window's OWN inspector/map pair: a frozen subject must not
        // share gesture or edit-session state with the live window's panes —
        // an alternating subject would cancel a drag on every frame.
        var graphical = ActivatorUtilities
            .CreateInstance<GraphicalBonePane>(services);
        _inspector = ActivatorUtilities
            .CreateInstance<PoseInspectorPane>(services);
        _inspector.DrawMapInline = graphical.DrawInline;
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
            float radius = Crystarium.ActiveTheme.Radii.Window;
            Crystarium.FloatingSurface.PrependShellBlur(
                dl, min, max, radius * s);
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, radius, shadow: false, blur: false);
            float headerBottom = DrawHeader(actor, min, max, s, dl);
            if (!_collapsed)
                DrawBody(
                    new Vector2(min.X, headerBottom), max, s);
            Crystarium.FloatingSurface.DrawBorder(min, max, radius);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    public override void OnClose()
    {
        base.OnClose();
        _selection.ForgetScope(_scope);
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
        float inset = theme.Page.Inset * s;
        float side = theme.Controls.ShellIconAction;
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
        float x = max.X - inset - side * s;
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
            new Vector2(min.X, min.Y + height),
            new Vector2(max.X, min.Y + height + rule),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return min.Y + height + rule;
    }

    private void DrawBody(Vector2 min, Vector2 max, float s)
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

        var contentOrigin = new Vector2(min.X + inset, min.Y + barHeight);
        var contentSize = new Vector2(
            MathF.Max(1f, max.X - min.X - inset * 2f),
            MathF.Max(1f, max.Y - contentOrigin.Y - inset));

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
