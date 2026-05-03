using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Dalamud Window subclass that hosts a Crystarium tree. Pushes the theme's
/// surface/border/text colors via ImGui style stack so callers don't need
/// to repeat the 14× PushStyleColor boilerplate. Override <see cref="Body"/>
/// with your Crystarium tree.
///
/// <code>
///   public class MyWindow : Crystarium.View
///   {
///       public MyWindow() : base("My Window") { Size = new Vector2(400, 300); }
///       protected override void Body()
///       {
///           Crystarium.Heading("Hello");
///           Crystarium.Card(() => Crystarium.Text("Body"));
///       }
///   }
/// </code>
/// </summary>
public abstract class View : Window
{
    private readonly bool _autoTheme;

    protected View(string title, ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse, bool autoTheme = true)
        : base(title, flags)
    {
        _autoTheme = autoTheme;
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    /// <summary>Override to render the window body with Crystarium primitives.</summary>
    protected abstract void Body();

    public sealed override void PreDraw()
    {
        base.PreDraw();
        if (_autoTheme) PushTheme();
    }

    public sealed override void Draw()
    {
        Body();
    }

    public sealed override void PostDraw()
    {
        if (_autoTheme) PopTheme();
        base.PostDraw();
    }

    private int _pushedColors;
    private int _pushedVars;

    private void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Color.Surface);              _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Color.Surface);               _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Color.SurfaceRaised);         _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Color.Text);                     _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Theme.Color.TextDim);          _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Color.Border);                 _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Theme.Color.SurfaceSunken);         _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Theme.Color.SurfaceRaised);   _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.Color.SurfaceSunken);         _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Color.SurfaceRaised);          _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Color.AccentHover);     _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Color.AccentActive);     _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.Header, Theme.Color.Accent);                 _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.Color.AccentHover);     _pushedColors++;
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Theme.Color.AccentActive);     _pushedColors++;

        float pad = Theme.Spacing.Md * PoserUI.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));    _pushedVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Theme.Radius.Sm * PoserUI.Scale); _pushedVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Theme.Radius.Md * PoserUI.Scale); _pushedVars++;
    }

    private void PopTheme()
    {
        if (_pushedVars > 0)   { ImGui.PopStyleVar(_pushedVars);   _pushedVars = 0; }
        if (_pushedColors > 0) { ImGui.PopStyleColor(_pushedColors); _pushedColors = 0; }
    }
}
