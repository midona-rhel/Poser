using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Poser.UI.Controls;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Modal popup. Pass a ref bool that toggles open/closed.
    /// <code>
    ///   if (Crystarium.Button("Open")) modalOpen = true;
    ///   Crystarium.Modal("##settings", ref modalOpen, "Settings", () => {
    ///       Crystarium.Heading("Section");
    ///       Crystarium.Text("body");
    ///   });
    /// </code>
    /// </summary>
    public static bool Modal(string id, ref bool open, string title, Action body, Vector2? minSize = null)
    {
        if (!open) return false;

        var size = (minSize ?? new Vector2(360, 240)) * PoserUI.Scale;
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos((displaySize - size) / 2f, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(size, new Vector2(float.MaxValue, float.MaxValue));

        // Theme-pushed via View if used, else push minimal modal chrome here.
        using var col1 = ImRaii.PushColor(ImGuiCol.WindowBg, Theme.Color.SurfaceRaised);
        using var col2 = ImRaii.PushColor(ImGuiCol.TitleBg, Theme.Color.SurfaceSunken);
        using var col3 = ImRaii.PushColor(ImGuiCol.TitleBgActive, Theme.Color.SurfaceRaised);
        using var col4 = ImRaii.PushColor(ImGuiCol.Border, Theme.Color.Border);
        using var var1 = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Theme.Radius.Md * PoserUI.Scale);
        using var var2 = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Spacing.Lg * PoserUI.Scale, Theme.Spacing.Lg * PoserUI.Scale));

        bool wasOpen = open;
        if (ImGui.Begin($"{title}##{id}", ref open, ImGuiWindowFlags.NoCollapse))
        {
            body();
        }
        ImGui.End();

        return wasOpen && !open; // returns true on the frame the modal closes
    }
}
