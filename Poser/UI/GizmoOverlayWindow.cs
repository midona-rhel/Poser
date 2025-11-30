using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Services;

namespace Poser.UI;

public class GizmoOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;

    private const int GizmoId = 142857;

    public GizmoOverlayWindow(IActorManager actorManager, ICameraService cameraService)
        : base("##poser_gizmo_overlay",
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

        // This window needs to be non-interactable except for the gizmo
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Position at top-left corner, spanning the entire screen
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);

        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;

        ImGuizmo.SetID(GizmoId);
    }

    public override void Draw()
    {
        var selectedActor = _actorManager.SelectedActor;
        if (selectedActor == null)
            return;

        // Get camera matrices
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Get actor transform
        var position = selectedActor.Position;
        var rotation = selectedActor.Rotation;

        // Create model matrix from actor transform
        var modelMatrix = Matrix4x4.CreateFromQuaternion(rotation);
        modelMatrix.Translation = position;

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.Enable(true);

        // Draw the gizmo
        // Note: We need a mutable copy of viewMatrix for ImGuizmo
        var viewMatrixCopy = viewMatrix;

        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            ImGuizmoOperation.Translate,
            ImGuizmoMode.World,
            ref modelMatrix))
        {
            // TODO: Apply the transform back to the actor
            // For now, we just draw the gizmo
        }
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }
}
