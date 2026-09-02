using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>Resize feedback on every resizable Poser window — the grip
/// and the lit border edge — is the theme accent, never Dalamud's global
/// highlight. Pushed in PreDraw, popped in PostDraw.</summary>
internal static class ResizeAccent
{
    public const int Count = 4;

    public static void Push()
    {
        var accent = Crystarium.ActiveTheme.Accent;
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, accent);
    }

    public static void Pop() => ImGui.PopStyleColor(Count);
}
