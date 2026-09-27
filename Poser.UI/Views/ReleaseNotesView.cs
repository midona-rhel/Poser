using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Settings;

namespace Poser.UI.Views;

public sealed class ReleaseNotesView(ReleaseNotesSession session)
{
    private static readonly string[] Highlights =
    [
        "Export idle poses with rotated, unevenly scaled bones without the transform-conversion error.",
        "Find Jaw and Jaw Only directly under Head in the skeleton tree.",
        "Right-click equipment or facewear to remove it, and dye or custom-colour swatches to clear them.",
        "Keep appearance colours in stable rows while editing or resizing the panel.",
        "Enter custom-colour brightness values above 1 in the colour picker.",
    ];

    public void Draw()
    {
        if (!session.IsOpen) return;
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(560, 380) * scale, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 220) * scale, new Vector2(float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        bool open = session.IsOpen;
        try
        {
            bool visible = ImGui.Begin("What's new###poser-release-notes", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
            try
            {
                if (!visible) return;
                var min = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                var owner = Interactive.BeginOwner("poser-release-notes", InteractionLayer.Window, min, min + size);
                try
                {
                    var frame = Crystarium.WindowFrame("release-notes", min, size, new WindowFrameProps
                    {
                        Title = "What's new", OnClose = session.Dismiss,
                        FooterRight = bar => bar.Button("Close", session.Dismiss),
                    });
                    ImGui.SetCursorScreenPos(frame.Body.Min);
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16) * scale);
                    try
                    {
                        bool bodyVisible = ImGui.BeginChild("##release-notes-body", frame.Body.Size,
                            false, ImGuiWindowFlags.AlwaysUseWindowPadding);
                        try { if (bodyVisible) { DrawBody(); session.MarkPresented(); } }
                        finally { ImGui.EndChild(); }
                    }
                    finally { ImGui.PopStyleVar(); }
                }
                finally { Interactive.EndOwner(owner); }
            }
            finally { ImGui.End(); }
        }
        finally { ImGui.PopStyleVar(2); if (!open) session.Dismiss(); }
    }

    private void DrawBody()
    {
        var theme = Crystarium.ActiveTheme;
        Crystarium.Text($"Poser {System.Version.Parse(session.Version).ToString(3)}",
            new TextStyle { Color = theme.TextDim });
        ImGui.Dummy(new Vector2(0, 8 * ImGuiHelpers.GlobalScale));
        foreach (var highlight in Highlights)
        {
            Crystarium.Text($"• {highlight}", default,
                TextConstraint.Wrap(ImGui.GetContentRegionAvail().X));
            ImGui.Dummy(new Vector2(0, 8 * ImGuiHelpers.GlobalScale));
        }
    }
}
