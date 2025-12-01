using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// </summary>
public class PropertiesPanel
{
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;

    public PropertiesPanel(IActorManager actorManager, IPosingService posingService)
    {
        _actorManager = actorManager;
        _posingService = posingService;
    }

    public void Draw()
    {
        if (ImGui.CollapsingHeader("Properties###properties_header", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var selected = _actorManager.PrimarySelectedActor;

            if (selected == null)
            {
                ImGui.TextDisabled("No entity selected");
                return;
            }

            DrawEntityProperties(selected);
        }
    }

    private void DrawEntityProperties(ActorBase actor)
    {
        var style = ImGui.GetStyle();

        // Name
        ImGui.Text("Name:");
        ImGui.SameLine(100);
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), actor.Name);

        // Type
        ImGui.Text("Type:");
        ImGui.SameLine(100);
        ImGui.TextDisabled("Actor");

        ImGui.Separator();

        // Transform section
        var transform = _posingService.GetEffectiveTransform(actor);

        // Position
        ImGui.Text("Position");
        var position = transform.Position;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.DragFloat3("##position", ref position, 0.01f))
        {
            var newTransform = transform;
            newTransform.Position = position;
            _posingService.SetTransformOverride(actor, newTransform);
        }

        // Rotation (display as euler angles)
        ImGui.Text("Rotation");
        var euler = QuaternionToEuler(transform.Rotation);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.DragFloat3("##rotation", ref euler, 1f))
        {
            var newTransform = transform;
            newTransform.Rotation = EulerToQuaternion(euler);
            _posingService.SetTransformOverride(actor, newTransform);
        }

        // Scale
        ImGui.Text("Scale");
        var scale = transform.Scale;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.DragFloat3("##scale", ref scale, 0.01f))
        {
            var newTransform = transform;
            newTransform.Scale = scale;
            _posingService.SetTransformOverride(actor, newTransform);
        }
    }

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
        // Convert quaternion to euler angles in degrees
        float sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        float cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float roll = System.MathF.Atan2(sinr_cosp, cosr_cosp);

        float sinp = 2 * (q.W * q.Y - q.Z * q.X);
        float pitch;
        if (System.MathF.Abs(sinp) >= 1)
            pitch = System.MathF.CopySign(System.MathF.PI / 2, sinp);
        else
            pitch = System.MathF.Asin(sinp);

        float siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        float yaw = System.MathF.Atan2(siny_cosp, cosy_cosp);

        return new Vector3(
            roll * 180f / System.MathF.PI,
            pitch * 180f / System.MathF.PI,
            yaw * 180f / System.MathF.PI);
    }

    private static Quaternion EulerToQuaternion(Vector3 euler)
    {
        // Convert euler angles (degrees) to quaternion
        float roll = euler.X * System.MathF.PI / 180f;
        float pitch = euler.Y * System.MathF.PI / 180f;
        float yaw = euler.Z * System.MathF.PI / 180f;

        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
    }
}
