using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Entities;
using Poser.Files;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Selective pose import/export controls hosted by the Pose rail.</summary>
public sealed class PoseFileInspectorSection
{
    private readonly IPoseFileService _poseFiles;
    private readonly SelectionSession _selection;
    private readonly ISkeletonService _skeletons;
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
        SelectionSession selection,
        ISkeletonService skeletons)
    {
        _poseFiles = poseFiles;
        _selection = selection;
        _skeletons = skeletons;
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    public float Draw(Vector2 cursor, float width, ISkeleton skeleton, float s)
    {
        float h = 0f;
        ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 5f * s), "Scope",
            11f, FontWeight.Regular, InspectorLayout.LabelColor);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + 46f * s, cursor.Y + h));
        Crystarium.Dropdown("##impex-scope",
            new[] { "Full", "Body", "Expression", "Selected" }, ref _scope);
        h += 32f * s;

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
                _poseFiles.ImportPose(
                    _skeletons.GetSkeletons(skeleton.Actor), path, BuildOptions());
            });
        }
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Export…",
                new ButtonProps { Id = "impex-export", Classes = Cls.Compact }))
        {
            _exportBrowser.Open(_lastPath, path =>
            {
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
                _poseFiles.ExportPose(
                    _skeletons.GetSkeletons(skeleton.Actor), path);
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
        // Scopes: 0 Full (every slot), 1 Body and 2 Expression
        // (Character-only), 3 Selected (the selected bones' exact slots via
        // the slot-qualified filter).
        bool full = _scope == 0, expression = _scope == 2, selected = _scope == 3;
        var options = new PoseImportOptions
        {
            ApplyRotation = _rotation,
            ApplyPosition = _position,
            ApplyScale = _scale,
            ApplyBody = true,
            AsExpression = expression,
            ApplyFace = full || selected,
            ApplyMainHand = full || selected,
            ApplyOffHand = full || selected,
            ApplyProp = full || selected,
            ApplyOrnament = full || selected,
            ResetBeforeImport = _reset,
            FilterIncludesDescendants = _descendants,
        };
        if (selected)
            options.BoneFilter = _selection.Selected
                .Where(id => id is { Kind: SceneEntityKind.Bone, Bone: not null })
                .Select(id => (id.Bone!.Value.Slot, id.Bone!.Value.CanonicalName))
                .ToHashSet();
        return options;
    }
}
