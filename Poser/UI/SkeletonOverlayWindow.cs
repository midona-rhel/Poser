using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Overlay window that draws skeleton bones on screen.
/// </summary>
public class SkeletonOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly ISkeletonService _skeletonService;

    // Configuration
    private const float BoneCircleSize = 4f;
    private const float LineThickness = 1.5f;

    // Colors (RGBA as uint)
    private static readonly uint LineColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.5f));       // White, semi-transparent
    private static readonly uint DotColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.9f));        // White, mostly opaque
    private static readonly uint DotOutlineColor = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.8f)); // Black outline

    public SkeletonOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        ISkeletonService skeletonService)
        : base("##poser_skeleton_overlay",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _actorManager = actorManager;
        _cameraService = cameraService;
        _skeletonService = skeletonService;

        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);

        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var viewportPos = ImGui.GetMainViewport().Pos;

        // Draw skeleton for each actor that has overlay visible
        foreach (var actor in _actorManager.Actors)
        {
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid || !skeleton.IsOverlayVisible)
                continue;

            // Update bone transforms from game memory
            skeleton.UpdateBoneTransforms();

            // Get model matrix for world-space conversion
            var modelMatrix = skeleton.GetModelMatrix();

            // Collect screen positions for all bones
            var boneScreenPositions = new Dictionary<IBone, Vector2>();

            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHiddenBone)
                    continue;

                // Transform bone position to world space
                var worldPos = Vector3.Transform(bone.LastTransform.Position, modelMatrix);

                // Convert to screen coordinates
                if (_cameraService.WorldToScreen(worldPos, out var screenPos))
                {
                    boneScreenPositions[bone] = viewportPos + screenPos;
                }
            }

            // Draw lines first (behind dots)
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHiddenBone || bone.ParentBone == null)
                    continue;

                if (!boneScreenPositions.TryGetValue(bone, out var bonePos))
                    continue;

                if (!boneScreenPositions.TryGetValue(bone.ParentBone, out var parentPos))
                    continue;

                // Draw line from parent to child
                drawList.AddLine(parentPos, bonePos, LineColor, LineThickness * ImGuiHelpers.GlobalScale);
            }

            // Draw dots on top
            foreach (var (bone, screenPos) in boneScreenPositions)
            {
                var scaledSize = BoneCircleSize * ImGuiHelpers.GlobalScale;

                // Draw outline
                drawList.AddCircle(screenPos, scaledSize + 1, DotOutlineColor, 12, 1.5f * ImGuiHelpers.GlobalScale);

                // Draw filled circle
                drawList.AddCircleFilled(screenPos, scaledSize, DotColor, 12);
            }
        }
    }
}
