using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Composition;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>A session-only pinned Properties host, with no workspace layout controls.</summary>
public sealed class PropertiesWindow : Window, IDisposable
{
    private readonly PropertiesContentLease _lease;
    private readonly AppShellViewModel _vm;
    private bool _collapsed, _resize;
    private float _expandedHeight = 640f;
    private float _width = 660f;

    public PropertiesWindow(PropertiesContentLease lease, Action settings)
        : base($"Properties###poser-properties-{Guid.NewGuid():N}",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground)
    {
        _lease = lease;
        _vm = new AppShellViewModel
        {
            OwnerId = WindowName,
            Detached = true,
            InspectorSplit = true,
            PropertiesOnly = true,
            OnSettings = settings,
            OnHideUi = () => IsOpen = false,
            OnCollapse = next => { _collapsed = next; _resize = true; },
        };
        lease.Content.Bind(_vm);
        Size = new(660f, 640f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        float height = _collapsed ? AppShellView.CollapsedBarHeight : 340f;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(520f, height),
            MaximumSize = new(float.MaxValue, _collapsed ? height : float.MaxValue),
        };
        if (_resize)
        {
            Size = new(_width, _collapsed ? height : _expandedHeight);
            SizeCondition = ImGuiCond.Always;
            _resize = false;
        }
        else SizeCondition = ImGuiCond.FirstUseEver;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Crystarium.ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
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
        _width = ImGui.GetWindowSize().X / scale;
        if (!_collapsed) _expandedHeight = ImGui.GetWindowSize().Y / scale;
        _vm.Collapsed = _collapsed;
        _lease.Content.Refresh();
        AppShellView.Draw(_vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        _lease.Content.DrawDialogs();
    }

    public void Dispose() => _lease.Dispose();
}
