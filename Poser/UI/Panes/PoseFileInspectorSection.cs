using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Entities;
using Poser.Files;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Selective pose import/export controls hosted by the Pose rail.</summary>
public sealed class PoseFileInspectorSection
{
    private readonly IPoseFileService _poseFiles;
    private readonly ISelectionService _selection;
    private readonly FileBrowser _importBrowser =
        new("Import Pose", new[] { ".pose", ".cmp" }, isSaveMode: false);
    private readonly FileBrowser _exportBrowser =
        new("Export Pose", new[] { ".pose" }, isSaveMode: true);
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private int _scope;
    private bool _rotation = true, _position = true, _scale;
    private bool _descendants = true, _reset;

    public PoseFileInspectorSection(
        IPoseFileService poseFiles,
        ISelectionService selection)
    {
        _poseFiles = poseFiles;
        _selection = selection;
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    public float Draw(Vector2 cursor, float width, ISkeleton skeleton, float s)
    {
        float h = 0f;
        ViewText.Label(cursor, "Import / Export", 11f, FontWeight.Regular,
            InspectorLayout.LabelColor);
        h += 20f * s;

        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        Crystarium.SegmentedControl("##impex-scope",
            new[] { "Full", "Body", "Expression", "Selected" }, ref _scope);
        h += 34f * s;

        if (_scope == 3)
        {
            ScopeCheck(cursor, h, 0f, "impx-desc", "Include descendants",
                ref _descendants, s);
            h += 28f * s;
        }

        float x = 0f;
        x += ScopeCheck(cursor, h, x, "impx-rot", "Rotation", ref _rotation, s);
        x += ScopeCheck(cursor, h, x, "impx-pos", "Position", ref _position, s);
        ScopeCheck(cursor, h, x, "impx-scale", "Scale", ref _scale, s);
        h += 28f * s;
        ScopeCheck(cursor, h, 0f, "impx-reset", "Reset affected bones first",
            ref _reset, s);
        h += 28f * s;

        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        if (Crystarium.Button("Import…",
                new ButtonProps { Id = "impex-import", Classes = Cls.Compact }))
        {
            _importBrowser.Open(_lastPath, path =>
            {
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
                _poseFiles.ImportPose(skeleton, path, BuildOptions());
            });
        }
        ImGui.SameLine(0f, 8f * s);
        if (Crystarium.Button("Export…",
                new ButtonProps { Id = "impex-export", Classes = Cls.Compact }))
        {
            _exportBrowser.Open(_lastPath, path =>
            {
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
                _poseFiles.ExportPose(skeleton, path);
            });
        }
        return h + 34f * s;
    }

    private static float ScopeCheck(
        Vector2 cursor,
        float h,
        float x,
        string id,
        string label,
        ref bool value,
        float s)
    {
        ImGui.SetCursorScreenPos(new Vector2(
            cursor.X + x * s, cursor.Y + h + 2f * s));
        Crystarium.Checkbox($"##{id}", ref value);
        float boxW = Crystarium.CheckboxSize / ImGuiHelpers.GlobalScale;
        ViewText.Label(new Vector2(
                cursor.X + (x + boxW + 6f) * s, cursor.Y + h + 3f * s),
            label, 11f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.72f));
        float labelW = ViewText.Measure(label, 11f) / s;
        ImGui.SetCursorScreenPos(new Vector2(
            cursor.X + (x + boxW + 4f) * s, cursor.Y + h));
        if (ImGui.InvisibleButton(
                $"##{id}-lbl", new Vector2((labelW + 6f) * s, 22f * s)))
            value = !value;
        return boxW + labelW + 20f;
    }

    private PoseImportOptions BuildOptions()
    {
        var options = new PoseImportOptions
        {
            ApplyRotation = _rotation,
            ApplyPosition = _position,
            ApplyScale = _scale,
            ApplyBody = _scope is 0 or 1 or 3,
            ApplyMainHand = _scope == 0,
            ApplyOffHand = _scope == 0,
            ApplyFace = _scope is 0 or 2 or 3,
            ResetBeforeImport = _reset,
            FilterIncludesDescendants = _descendants,
        };
        if (_scope == 3)
            options.BoneFilter = _selection.GetSelected<IBone>()
                .Select(bone => bone.BoneName)
                .ToHashSet(StringComparer.Ordinal);
        return options;
    }
}
