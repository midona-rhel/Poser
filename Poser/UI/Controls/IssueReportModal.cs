using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Diagnostics;

namespace Poser.UI.Controls;

/// <summary>
/// "Report an issue": says what the report holds, offers the scene as an
/// option with its own plain caveat, and saves. The saved file's folder
/// opens so the user can attach it. The dialog polls the service while a
/// scene save runs, so the zip closes with the scene inside.
/// </summary>
public sealed class IssueReportModal
{
    private const string Intro =
        "Saves a file you can attach to an issue: the last five hundred "
        + "actions with their values, the notices, any errors, versions, "
        + "settings and Poser's own log lines. Character names are "
        + "replaced by Actor 1, Actor 2 and so on. Nothing is sent anywhere.";
    private const string SceneNote =
        "Scene data only: poses, looks, lights, cameras, the environment. "
        + "No modified files and no mods are included.";
    private const float Gap = 10f;

    private readonly IssueReportService _reports;
    private readonly UserNotices _notices;
    private bool _open;
    private bool _includeScene;
    private bool _saving;

    public IssueReportModal(IssueReportService reports, UserNotices notices)
    {
        _reports = reports;
        _notices = notices;
    }

    public void Open()
    {
        _open = true;
        _saving = false;
    }

    public void Draw()
    {
        if (_saving)
            _reports.Tick();
        if (!_open)
            return;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var style = new TextStyle { Size = theme.Typography.LabelSize, Color = theme.Text };
        var hint = new TextStyle { Size = theme.Typography.CaptionSize, Color = theme.FormHint };
        Crystarium.Modal(
            "##issue-report",
            _open,
            next => _open = next,
            "Report an issue",
            size: ModalSize.Medium,
            body: () =>
            {
                Paragraph(Intro, style, scale);
                ImGui.Dummy(new Vector2(0f, Gap * scale));
                Crystarium.Checkbox(
                    "##issue-include-scene", _includeScene,
                    next => _includeScene = next,
                    help: "Add the scene to the report");
                ImGui.SameLine(0f, 8f * scale);
                Crystarium.TextAt(ImGui.GetCursorScreenPos(), "Include the scene", style);
                ImGui.Dummy(new Vector2(0f, Gap * scale));
                Paragraph(SceneNote, hint, scale);
            },
            footer: () =>
            {
                if (Crystarium.Button("Cancel", id: "issue-cancel", disabled: _saving))
                    _open = false;
                ImGui.SameLine(0f, 8f * scale);
                if (Crystarium.Button(
                        _saving ? "Saving…" : "Save report",
                        variant: ButtonVariant.Primary,
                        disabled: _saving,
                        help: "Write the report and open its folder",
                        id: "issue-save"))
                {
                    _saving = true;
                    _reports.Save(_includeScene, Done, Failed);
                    if (!_reports.Pending)
                        _saving = false;
                }
            });
    }

    private void Done(string zip)
    {
        _saving = false;
        _open = false;
        _notices.Done($"Saved the issue report to {zip}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zip}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // The notice already names the file.
        }
    }

    private void Failed(string detail)
    {
        _saving = false;
        _notices.Failed("Issue report", detail);
    }

    private static void Paragraph(string text, TextStyle style, float scale)
    {
        float width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var constraint = TextConstraint.Wrap(width / scale);
        var size = Crystarium.MeasureText(text, style, constraint);
        float height = MathF.Max(size.Y, (style.Size ?? 14f) * scale);
        Crystarium.TextInBand(origin, new Vector2(width, height), text, style, constraint, TextAlign.Start);
        ImGui.Dummy(new Vector2(width, height));
    }
}
