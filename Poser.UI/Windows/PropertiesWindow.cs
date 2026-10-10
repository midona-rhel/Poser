using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Composition;
using Poser.UI.Controls;
using Poser.UI.Views;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI;

// Position is in screen pixels; size is the expanded content size in logical
// units. The collapsible inspector adds its own width, never shrinking content.
internal readonly record struct PropertiesWindowPlacement(Vector2 Position, Vector2 Size);

/// <summary>A session-only pinned Properties host, with no workspace layout controls.</summary>
public sealed class PropertiesWindow : Window, IDisposable
{
    private readonly PropertiesContentLease _lease;
    private readonly AppShellViewModel _vm;
    private readonly Action<PropertiesWindowPlacement> _rememberPlacement;
    private PropertiesWindowPlacement _reportedPlacement;
    private bool _collapsed, _resize;
    private bool _inspectorCollapsed;
    private bool? _pendingInspectorCollapsed;
    private float InspectorWidth => _inspectorCollapsed ? 0f : AppShellView.RailWidth;
    private float _expandedHeight = 640f;
    private float _width = 660f;

    internal PropertiesWindow(PropertiesContentLease lease, Action settings,
        PropertiesWindowPlacement placement, Action<PropertiesWindowPlacement> rememberPlacement)
        : base($"Properties###poser-properties-{Guid.NewGuid():N}",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground)
    {
        _lease = lease;
        _rememberPlacement = rememberPlacement;
        _reportedPlacement = placement;
        _width = placement.Size.X;
        _expandedHeight = placement.Size.Y;
        _vm = new AppShellViewModel
        {
            OwnerId = WindowName,
            Detached = true,
            PropertiesOnly = true,
            OnSettings = settings,
            OnHideUi = () => IsOpen = false,
            OnCollapse = next => { _collapsed = next; _resize = true; },
            OnInspectorCollapseToggle = () => _pendingInspectorCollapsed = !_inspectorCollapsed,
        };
        lease.Content.Bind(_vm);
        Size = placement.Size + new Vector2(InspectorWidth, 0f);
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = placement.Position;
        PositionCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        PositionCondition = ImGuiCond.FirstUseEver;
        if (_pendingInspectorCollapsed is { } next)
        {
            _inspectorCollapsed = next;
            _vm.InspectorCollapsed = next;
            _pendingInspectorCollapsed = null;
            _resize = true;
            if (!next)
            {
                var viewport = ImGui.GetMainViewport();
                var extent = new Vector2(_width + InspectorWidth,
                    _collapsed ? AppShellView.CollapsedBarHeight : _expandedHeight) * ImGuiHelpers.GlobalScale;
                Position = Vector2.Clamp(_reportedPlacement.Position, viewport.WorkPos,
                    Vector2.Max(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - extent));
                PositionCondition = ImGuiCond.Always;
            }
        }
        float height = _collapsed ? AppShellView.CollapsedBarHeight : 340f;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(AppShellView.MinimumPropertiesWidth + InspectorWidth, height),
            MaximumSize = new(float.MaxValue, _collapsed ? height : float.MaxValue),
        };
        if (_resize)
        {
            Size = new(_width + InspectorWidth, _collapsed ? height : _expandedHeight);
            SizeCondition = ImGuiCond.Always;
            _resize = false;
        }
        else SizeCondition = ImGuiCond.FirstUseEver;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
        ResizeAccent.Push();
    }

    public override void PostDraw()
    {
        ResizeAccent.Pop();
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        float scale = ImGuiHelpers.GlobalScale;
        _width = ImGui.GetWindowSize().X / scale - InspectorWidth;
        if (!_collapsed) _expandedHeight = ImGui.GetWindowSize().Y / scale;
        var placement = new PropertiesWindowPlacement(
            ImGui.GetWindowPos(), new(_width, _expandedHeight));
        // Only a geometry change updates the shared spawn anchor: draw order
        // must not let an untouched older window win over the one just moved.
        if (placement != _reportedPlacement)
        {
            _reportedPlacement = placement;
            _rememberPlacement(placement);
        }
        _vm.Collapsed = _collapsed;
        _lease.Content.Refresh();
        if (!ManipulationHide.Hidden || ManipulationDrag.ShellHeld)
        {
            using var manipulationFade = ManipulationHide.FadeScope();
            AppShellView.Draw(_vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        }
        _lease.Content.DrawDialogs();
    }

    public void Dispose() => _lease.Dispose();
    internal void PumpInteraction(bool pointerHeld)
    {
        _lease.Content.PumpInteraction(pointerHeld);
        _lease.Content.PumpInspectorInteraction(pointerHeld,
            IsOpen && !_collapsed && !_inspectorCollapsed);
    }

    internal static PropertiesWindowPlacement Cascade(
        PropertiesWindowPlacement? previous, Vector2 firstAnchor)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport();
        var minimum = new Vector2(AppShellView.MinimumPropertiesWidth, 340f);
        var size = Vector2.Clamp(previous?.Size ?? new(660f, 640f),
            minimum, Vector2.Max(minimum, viewport.WorkSize / scale - new Vector2(AppShellView.RailWidth, 0f)));
        var offset = new Vector2(24f * scale);
        var position = (previous?.Position ?? firstAnchor) + offset;
        var last = Vector2.Max(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize
            - (size + new Vector2(AppShellView.RailWidth, 0f)) * scale);
        // Restart each overflowing axis instead of stacking every new window
        // against the same bottom/right edge.
        if (position.X > last.X) position.X = viewport.WorkPos.X + offset.X;
        if (position.Y > last.Y) position.Y = viewport.WorkPos.Y + offset.Y;
        return new(Vector2.Clamp(position, viewport.WorkPos, last), size);
    }
}
