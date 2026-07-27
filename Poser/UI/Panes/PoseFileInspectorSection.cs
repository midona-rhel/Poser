using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Entities;
using Poser.Files;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Game.Posing;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Selective pose import/export controls hosted by the Pose rail.</summary>
public sealed class PoseFileInspectorSection
{
    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
    private readonly ISkeletonService _skeletons;
    private string _status = string.Empty;
    private readonly Crystarium.FileDialog _importBrowser =
        new("Import Pose", new[] { ".pose", ".cmp" }, isSaveMode: false);
    private readonly Crystarium.FileDialog _exportBrowser =
        new("Export Pose", new[] { ".pose" }, isSaveMode: true);
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private int _scope;
    private bool _rotation = true, _position = true, _scale;
    private bool _descendants = true, _reset;

    public PoseFileInspectorSection(
        IPoseFileService poseFiles,
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        ISkeletonService skeletons)
    {
        _poseFiles = poseFiles;
        _poseFacade = poseFacade;
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
            new[] { "Full", "Body", "Expression", "Selected" },
            _scope,
            next => _scope = next);
        h += 32f * s;

        if (_scope == 3)
        {
            ScopeCheck(cursor, h, 0f, "impx-desc", "Include descendants",
                ref _descendants, s);
            h += 28f * s;
        }

        float x = 0f;
        x += ScopeCheck(cursor, h, x, "impx-pos", "Translation", ref _position, s);
        x += ScopeCheck(cursor, h, x, "impx-rot", "Rotation", ref _rotation, s);
        ScopeCheck(cursor, h, x, "impx-scale", "Scale", ref _scale, s);
        h += 28f * s;
        ScopeCheck(cursor, h, 0f, "impx-reset", "Reset affected bones first",
            ref _reset, s);
        h += 28f * s;

        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + h));
        if (Crystarium.Button("Import…", id: "impex-import",
                style: ControlStyle.Workspace))
        {
            // The actor is frozen at dialog open; the Selected-scope
            // selection freezes as COMPLETE BoneIds at dialog confirmation.
            // The facade verifies every one belongs to the frozen actor's
            // exact generation — a mismatched or stale selection fails, it
            // never becomes a name-based selection on another actor.
            _importBrowser.Open(_lastPath, path =>
            {
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
                List<BoneId>? frozenSelection = null;
                if (_scope == 3)
                    frozenSelection = _selection.Selected
                        .Where(id => id is { Kind: SceneEntityKind.Bone, Bone: not null })
                        .Select(id => id.Bone!.Value)
                        .ToList();
                var imported = _poseFacade.ImportPose(
                    skeleton.Actor, path, BuildOptions(), frozenSelection);
                _status = imported.Success
                    ? string.Empty
                    : $"Import: {imported.Detail}";
            });
        }
        ImGui.SameLine(0f, 6f * s);
        if (Crystarium.Button("Export…", id: "impex-export",
                style: ControlStyle.Workspace))
        {
            _exportBrowser.Open(_lastPath, path =>
            {
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
                bool exported = _poseFiles.ExportPose(
                    _skeletons.GetSkeletons(skeleton.Actor), path);
                _status = exported ? string.Empty : "Export: the pose file could not be written.";
            });
        }
        h += 34f * s;

        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + h + 2f * s),
                _status, 11f, FontWeight.Regular, InspectorLayout.HintColor);
            h += 20f * s;
        }
        return h;
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
        bool next = value;
        Crystarium.Checkbox($"##{id}", value, changed => next = changed);
        value = next;
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
        // The Selected-scope bone filter is NOT built here: the frozen
        // BoneIds travel to the facade, which verifies actor identity and
        // exact generations before reducing them to a slot-qualified
        // filter.
        return options;
    }
}
