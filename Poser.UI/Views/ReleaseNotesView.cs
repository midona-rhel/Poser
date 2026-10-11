using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Settings;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.WindowFrameWidgets;

namespace Poser.UI.Views;

public sealed class ReleaseNotesView(ReleaseNotesSession session)
{
    private int _shownRevision = -1;

    public void Draw()
    {
        if (!session.IsOpen) return;
        if (session.ShowingHistory && _shownRevision != session.ViewRevision)
            ImGui.SetNextWindowFocus();
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
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
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
                    var frame = WindowFrame("release-notes", min, size, new WindowFrameProps
                    {
                        Title = session.ShowingHistory ? "Release notes" : "What's new",
                        OnClose = session.Dismiss,
                        FooterRight = bar =>
                        {
                            if (!session.ShowingHistory) bar.Button("All releases", session.Open);
                            bar.Button("Close", session.Dismiss);
                        },
                    });
                    ImGui.SetCursorScreenPos(frame.Body.Min);
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16) * scale);
                    try
                    {
                        bool bodyVisible = ImGui.BeginChild("##release-notes-body", frame.Body.Size,
                            false, ImGuiWindowFlags.AlwaysUseWindowPadding);
                        try
                        {
                            if (bodyVisible)
                            {
                                if (_shownRevision != session.ViewRevision)
                                {
                                    ImGui.SetScrollY(0);
                                    _shownRevision = session.ViewRevision;
                                }
                                DrawBody();
                                session.MarkPresented();
                            }
                        }
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
        var theme = ActiveTheme;
        if (session.Entries.Count == 0)
            Text("No release notes are bundled for this version.");
        foreach (var release in session.Entries)
        {
            Text($"Poser {release.Version.ToString(3)} — {release.Title}",
                new TextStyle
                {
                    Color = theme.Text,
                    Size = theme.Typography.HeadingSize,
                    Weight = FontWeight.SemiBold,
                },
                TextConstraint.Wrap(ImGui.GetContentRegionAvail().X));
            ImGui.Dummy(new Vector2(0, 8 * ImGuiHelpers.GlobalScale));
            foreach (var highlight in release.Highlights)
            {
                Text($"• {highlight}", default,
                    TextConstraint.Wrap(ImGui.GetContentRegionAvail().X));
                ImGui.Dummy(new Vector2(0, 8 * ImGuiHelpers.GlobalScale));
            }
            ImGui.Dummy(new Vector2(0, 12 * ImGuiHelpers.GlobalScale));
        }
    }
}
