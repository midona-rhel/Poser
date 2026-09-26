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
        "Attach scene objects to actors or bones so they follow your pose.",
        "Generated body colliders now follow the actor's bones.",
        "Drawing colliders is much faster, especially with several on screen.",
        "Duplicated colliders keep their settings and bone attachments.",
        "Switch duplicated actors to a different Penumbra collection.",
        "MCDF actors can now use your modded animations.",
        "New free cameras preserve the view you're looking through.",
    ];

    public void Draw()
    {
        Crystarium.Modal("##release-notes", session.IsOpen,
            open => { if (!open) session.Dismiss(); },
            "What's new", DrawBody,
            () => Crystarium.Button("Close", session.Dismiss,
                ButtonVariant.Primary, ControlStyle.Comfortable),
            ModalSize.Medium);
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
        // The auto-sized modal measures off screen on its first frame.
        if (ImGui.GetWindowPos().X >= 0)
            session.MarkPresented();
    }
}
