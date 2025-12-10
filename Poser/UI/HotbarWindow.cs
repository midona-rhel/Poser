using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Services;
using Poser.UI.Components;

namespace Poser.UI;

public class HotbarWindow : Window
{
    private const float HotbarHeight = 40f;
    private const float SidebarWidth = 560f;

    private readonly IGPoseService _gPoseService;
    private readonly Hotbar _hotbar;

    public HotbarWindow(
        IGPoseService gPoseService,
        IEditorState editorState,
        IActorManager actorManager,
        ISkeletonService skeletonService)
        : base("###poser_hotbar_window",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBringToFrontOnFocus)
    {
        _gPoseService = gPoseService;
        _hotbar = new Hotbar(editorState);
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var displaySize = ImGui.GetIO().DisplaySize;
        var scaledHeight = HotbarHeight * ImGuiHelpers.GlobalScale;

        // Position at bottom, spanning width except sidebar
        Position = new Vector2(0, displaySize.Y - scaledHeight);
        Size = new Vector2(displaySize.X - SidebarWidth, scaledHeight);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Size.Value,
            MaximumSize = Size.Value
        };
    }

    public override bool DrawConditions()
    {
        return _gPoseService.IsGPosing;
    }

    public override void Draw()
    {
        _hotbar.Draw();
    }
}
