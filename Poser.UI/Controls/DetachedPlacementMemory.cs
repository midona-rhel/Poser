using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI.Controls;

internal sealed class DetachedPlacementMemory(ConfigurationService configuration, string key)
{
    public WindowPlacement? Saved =>
        configuration.Config.UI.DetachedPlacements.TryGetValue(key, out var saved) ? saved : null;

    public void Remember(Vector2 position, Vector2 size)
    {
        // Persist the finished move/resize, not every frame of its drag.
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        var next = new WindowPlacement(position, size);
        if (next == Saved) return;
        configuration.Config.UI.DetachedPlacements[key] = next;
        configuration.Save(notify: false);
    }
}
