using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    public static void Icon(TablerIcon icon, float size, Vector4? color = null, bool flipX = false)
    {
        var doc = Tabler.Get(icon);
        if (doc == null)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size);
        doc.Render(ImGui.GetWindowDrawList(), min, max, color ?? ActiveTheme.Text, flipX);
        ImGui.Dummy(new Vector2(size));
    }

    public static void Icon(string name, float size, Vector4? color = null)
    {
        var doc = Tabler.Get(name);
        if (doc == null)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size);
        doc.Render(ImGui.GetWindowDrawList(), min, max, color ?? ActiveTheme.Text);
        ImGui.Dummy(new Vector2(size));
    }
}
