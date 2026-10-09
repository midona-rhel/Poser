using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Presentation;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.UI;

public sealed partial class GraphicalBonePane
{
    private readonly Guid[] _selectedPresets = new Guid[2];
    private readonly List<(PortableBoneId Bone, string Section)> _drawnPoints = new();
    private IReadOnlyList<BoneMapPoint>? _layout;
    private Vector2 _mapOrigin, _mapSize;
    private string _pointSection = "body";
    private bool _drawingEditor, _addingCustomPoint;
    private BoneMapPresetDraft? _draft;
    private bool _editDefault;
    private string? _mapBackground;
    private string? _mapTemplate;
    private List<BoneMapPoint> _editorDefaults = new();
    private ActorId? _editActor;
    private string _editFilter = string.Empty;
    private string? _editError;
    private readonly string _editorId = "bone-map-editor-" + Guid.NewGuid().ToString("N");
    private (PortableBoneId Bone, string Section)? _dragPoint, _contextPoint;
    private Vector2 _dragOffset;
    private readonly object _editorHoverOwner = new();

    private void DrawPresetActions(int page, ActorDescriptor actor, Vector2 origin, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var presets = EditorPresets(actor, (BoneMapKind)page);
        var names = presets.Select(item => item.Name).ToArray();
        int selected = Math.Max(0, Array.FindIndex(presets, item => item.Id == SelectedPreset(page, actor)?.Id));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - 200f * scale, 0f));
        Crystarium.Dropdown("##map-preset", names, selected,
            index => _selectedPresets[page] = presets[index].Id,
            ControlStyle.Workspace with { Width = UiWidth.Fixed(136f) });
        ImGui.SetCursorScreenPos(origin + new Vector2(width - 58f * scale, 0f));
        if (Crystarium.Button("Edit", disabled: _draft != null,
            help: _draft != null ? "Finish the open map editor first" : null,
            style: ControlStyle.Workspace with { Width = UiWidth.Fixed(58f) }))
        {
            _openEditorPage = page;
            _editActor = actor.Id;
        }
    }

    private int? _openEditorPage;

    public void DrawEditor()
    {
        if (_openEditorPage is { } opening)
        {
            _openEditorPage = null;
            if (_editActor is not { } editingId || _scene.Snapshot.FindActor(editingId) is not { } editingActor) return;
            var preset = SelectedPreset(opening, editingActor);
            _editorDefaults = BuildDefaults(editingActor, (BoneMapKind)opening, preset?.Template);
            _editDefault = preset == null || BoneMapTemplates.IsBuiltIn(preset.Id);
            _draft = new((BoneMapKind)opening, _editorDefaults, preset);
            _editFilter = string.Empty;
            _editError = null;
        }
        if (_draft == null) return;
        var actor = _editActor is { } id ? _scene.Snapshot.FindActor(id) : null;
        if (actor != null && !IsHumanoid(actor.Id)) actor = null;
        bool open = true;
        float scale = ImGuiHelpers.GlobalScale;
        Crystarium.FloatingSurface.Window(_editorId, ref open,
            MathF.Min(1040f, ImGui.GetIO().DisplaySize.X / scale - 24f),
            MathF.Min(670f, ImGui.GetIO().DisplaySize.Y / scale - 24f), frame =>
            {
                if (_draft == null) return;
                var rects = Crystarium.WindowFrame(_editorId, frame.Min, frame.Size, new WindowFrameProps
                {
                    Title = $"Edit {_draft.Kind} map",
                    OnClose = CloseEditor,
                    HostPaintsChrome = true,
                    BandHeight = 44f,
                    RailWidth = 284f,
                    FooterLeft = left => left.Label(_editError ?? (_editDefault
                        ? "Built-in template is locked. Choose New or Copy to edit."
                        : "Drag points to arrange them. Right-click for actions.")),
                    FooterRight = right =>
                    {
                        right.Button("Cancel", CloseEditor, style: ControlStyle.Comfortable);
                        right.Button("Confirm", () => { if (_editDefault || SaveEditor()) CloseEditor(); },
                            style: ControlStyle.Comfortable, variant: ButtonVariant.Primary);
                    },
                });
                if (_draft == null) return;
                DrawEditorPresetToolbar(rects.Band);
                DrawEditorBody(actor, rects.Rail, rects.Body);
            }, exclusive: false);
        if (!open) CloseEditor();
    }

    private bool SaveEditor()
    {
        if (_editDefault || _draft is not { } draft) return false;
        _editError = draft.Save(_configuration.Config.Skeleton.BoneMapPresets, out var saved);
        if (_editError != null) return false;
        _selectedPresets[(int)draft.Kind] = saved;
        _configuration.Save();
        LoadEditorPreset(_configuration.Config.Skeleton.BoneMapPresets.First(item => item.Id == saved));
        return true;
    }

    private void CreateEditorPreset(bool copy)
    {
        if (_draft is not { } current) return;
        var store = _configuration.Config.Skeleton.BoneMapPresets;
        var created = new BoneMapPresetDraft(current.Kind, _editorDefaults,
            initial: copy ? current.Points : _editorDefaults)
        {
            Name = BoneMapPresetDraft.UniqueName(store, current.Kind,
                copy ? $"{current.Name} copy" : "New preset"),
            Background = copy ? current.Background : null,
            Template = current.Template,
        };
        _editError = created.Save(store, out var id);
        if (_editError != null) return;
        _configuration.Save();
        LoadEditorPreset(store.First(item => item.Id == id));
    }

    private void CloseEditor()
    {
        _draft = null;
        _editActor = null;
        _dragPoint = null;
        _contextPoint = null;
        _presentation.PublishMapHover(_editorHoverOwner, null);
        Crystarium.FloatingMenu.Dismiss(_editorId + "-point");
    }

    private void DrawEditorBody(ActorDescriptor? actor, WindowFrameRect rail, WindowFrameRect body)
    {
        if (_draft is not { } draft) return;
        float s = ImGuiHelpers.GlobalScale;
        var origin = rail.Min + new Vector2(12f * s);
        ImGui.SetCursorScreenPos(origin);
        if (actor == null)
        {
            Crystarium.Text("The original actor is no longer available. Close and reopen the editor.");
            return;
        }
        float left = rail.Size.X - 24f * s;
        float height = MathF.Max(100f * s, rail.Size.Y - 24f * s);
        Crystarium.FilterPill("##available-bones", _editFilter, value => _editFilter = value, "Search bones",
            ControlStyle.Workspace with { Width = UiWidth.Region(left / s) });
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, 36f * s));
        var availableBones = AvailableBones(actor);
        var duplicates = availableBones.Values.GroupBy(bone => bone.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Crystarium.ScrollRegion("##map-bones", left / s, height / s - 36f, scroll =>
        {
            foreach (var bone in availableBones.Values
                .Where(bone => _configuration.Config.Display.ShowNsfwBones || !Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName))
                .Where(bone => bone.DisplayName.Contains(_editFilter, StringComparison.OrdinalIgnoreCase)
                    || bone.Id.CanonicalName.Contains(_editFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(bone => bone.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(bone => bone.Id.Slot).ThenBy(bone => bone.Id.PartialId))
            {
                var key = PortableBoneId.From(bone.Id);
                var row = ImGui.GetCursorScreenPos();
                float inset = Crystarium.ActiveTheme.Controls.InputPaddingX * s;
                float rowHeight = 28f * s;
                var checkbox = Crystarium.MeasureCheckbox();
                ImGui.SetCursorScreenPos(row + new Vector2(scroll.ContentWidth * s - checkbox.X - inset,
                    (rowHeight - checkbox.Y) * 0.5f));
                ImGui.PushID(bone.Id.ToString());
                Crystarium.Checkbox("include", draft.Contains(key), on =>
                { if (on) draft.Add(key); else draft.Remove(key); }, disabled: _editDefault);
                var nameOrigin = row + new Vector2(inset, 0f);
                float nameWidth = scroll.ContentWidth * s - 2f * inset - checkbox.X - 8f * s;
                string label = $"{bone.DisplayName} — {bone.Id.CanonicalName}";
                if (duplicates.Contains(bone.DisplayName)) label += $" · {bone.Id.Slot}/{bone.Id.PartialId}";
                Crystarium.TextInBand(nameOrigin, new Vector2(nameWidth, rowHeight), label,
                    default, TextConstraint.Truncate(nameWidth));
                if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(nameOrigin, nameOrigin + new Vector2(nameWidth, 26f * s)))
                {
                    Crystarium.HoverHelp.Preview("bone-name", nameOrigin, nameOrigin + new Vector2(nameWidth, 26f * s),
                        $"{bone.DisplayName} · {bone.Id.CanonicalName} · {bone.Id.Slot}/{bone.Id.PartialId}");
                    if (!_editDefault && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        string section = draft.Points.FirstOrDefault(point => point.Bone == key)?.Section
                            ?? _editorDefaults.FirstOrDefault(point => point.Bone == key)?.Section
                            ?? (draft.Kind == BoneMapKind.Body ? "body" : "face");
                        _contextPoint = (key, section);
                        Crystarium.FloatingMenu.Dismiss(_editorId + "-point");
                        Crystarium.FloatingMenu.Open(_editorId + "-point", ImGui.GetMousePos(), PointMenu(draft, _contextPoint.Value));
                    }
                }
                ImGui.PopID();
                ImGui.SetCursorScreenPos(row + new Vector2(0f, rowHeight));
            }
        });
        ImGui.SetCursorScreenPos(body.Min + new Vector2(12f * s));
        string[] backgrounds = draft.Kind == BoneMapKind.Face
            ? ["Auto", "Human", "Miqo’te", "Viera", "Hrothgar", "None"] : ["Illustrations", "None"];
        string?[] values = draft.Kind == BoneMapKind.Face
            ? [null, "PoseHeadWithEars", "PoseHeadMiqote", "PoseHeadVieraFloppy", "PoseHeadHroth", "none"] : [null, "none"];
        Crystarium.Dropdown("Background", backgrounds, Math.Max(0, Array.IndexOf(values, draft.Background)),
            index => draft.Background = values[index],
            ControlStyle.Workspace with { Width = UiWidth.Fixed(180f) }, disabled: _editDefault);
        ImGui.SetCursorScreenPos(body.Min + new Vector2(12f * s, 48f * s));
        var canvas = Vector2.Max(Vector2.One, body.Size - new Vector2(24f * s, 60f * s));
        ImGui.BeginChild("##map-preview", canvas, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try { DrawMap((int)draft.Kind, canvas, actor); }
        finally { ImGui.EndChild(); }
    }

    private void DrawEditorPresetToolbar(WindowFrameRect band)
    {
        if (_draft is not { } draft) return;
        var actor = _editActor is { } actorId ? _scene.Snapshot.FindActor(actorId) : null;
        if (actor == null) return;
        var presets = EditorPresets(actor, draft.Kind);
        string[] names = presets.Select(item => item.Name).ToArray();
        int selected = Math.Max(0, Array.FindIndex(presets, item => item.Id == draft.SourceId));
        bool dirty = !_editDefault && draft.HasChanges;
        float s = ImGuiHelpers.GlobalScale;
        float height = Crystarium.ActiveTheme.Controls.WorkspaceHeight;
        var row = band.Min + new Vector2(12f * s, (band.Size.Y - height * s) * 0.5f);
        float width = band.Size.X / s - 24f;
        float selectorWidth = MathF.Min(220f, (width - 376f) * 0.42f);
        float nameWidth = MathF.Min(280f, width - selectorWidth - 376f);
        var style = ControlStyle.Workspace;
        ImGui.SetCursorScreenPos(row);
        string preview = draft.Name;
        Crystarium.ActionDropdown("##edit-map-preset", names, selected, preview,
            index => LoadEditorPreset(presets[index]),
            style with { Width = UiWidth.Fixed(selectorWidth) }, disabled: dirty,
            help: dirty ? "Save or discard these edits before switching presets" : null);
        // The selector may have replaced the draft on this frame.
        draft = _draft!;
        ImGui.SetCursorScreenPos(row + new Vector2((selectorWidth + 8f) * s, 0));
        Crystarium.TextInput("##map-preset-name", draft.Name, value => draft.Name = value,
            style with { Width = UiWidth.Fixed(nameWidth) }, placeholder: "Preset name", disabled: _editDefault);
        float next = selectorWidth + nameWidth + 16f;
        ImGui.SetCursorScreenPos(row + new Vector2(next * s, 0));
        if (Crystarium.Button("New", style: style with { Width = UiWidth.Fixed(56f) }, disabled: dirty, id: "new-map-preset"))
        {
            CreateEditorPreset(copy: false);
            return;
        }
        ImGui.SetCursorScreenPos(row + new Vector2((next + 64f) * s, 0));
        if (Crystarium.Button("Copy", style: style with { Width = UiWidth.Fixed(56f) }, id: "copy-map-preset"))
        {
            CreateEditorPreset(copy: true);
            return;
        }
        ImGui.SetCursorScreenPos(row + new Vector2((next + 128f) * s, 0));
        if (Crystarium.Button("Save", style: style with { Width = UiWidth.Fixed(56f) }, disabled: _editDefault, id: "save-map-edits"))
        {
            SaveEditor();
            return;
        }
        ImGui.SetCursorScreenPos(row + new Vector2((next + 192f) * s, 0));
        if (Crystarium.Button("Discard", style: style with { Width = UiWidth.Fixed(72f) }, disabled: _editDefault, id: "discard-map-edits"))
        {
            LoadEditorPreset(presets.FirstOrDefault(item => item.Id == draft.SourceId));
            return;
        }
        ImGui.SetCursorScreenPos(row + new Vector2((width - 64f) * s, 0));
        if (Crystarium.Button("Delete", style: style with { Width = UiWidth.Fixed(64f) }, disabled: _editDefault,
            id: "delete-map-preset", help: "Delete this custom map preset"))
        {
            _editError = draft.Delete(_configuration.Config.Skeleton.BoneMapPresets);
            if (_editError == null)
            {
                _configuration.Save();
                LoadEditorPreset(null);
            }
        }
    }

    private void LoadEditorPreset(BoneMapPreset? preset)
    {
        if (_draft is not { } draft) return;
        if (_editActor is { } id && _scene.Snapshot.FindActor(id) is { } actor)
        {
            if (preset == null)
            {
                _selectedPresets[(int)draft.Kind] = Guid.Empty;
                preset = SelectedPreset((int)draft.Kind, actor);
            }
            _editorDefaults = BuildDefaults(actor, draft.Kind, preset?.Template);
        }
        _editDefault = preset == null || BoneMapTemplates.IsBuiltIn(preset.Id);
        _draft = new(draft.Kind, _editorDefaults, preset);
        _selectedPresets[(int)draft.Kind] = preset?.Id ?? Guid.Empty;
        _dragPoint = null;
        _contextPoint = null;
        _editError = null;
        Crystarium.FloatingMenu.Dismiss(_editorId + "-point");
    }

    private void DrawAdditionalPoints(ActorDescriptor actor)
    {
        if (_layout == null) return;
        var available = AvailableBones(actor);
        _addingCustomPoint = true;
        try
        {
            foreach (var point in _layout)
            {
                if (_drawnPoints.Contains((point.Bone, point.Section)) || !available.TryGetValue(point.Bone, out var bone)) continue;
                _pointSection = point.Section;
                DrawBoneAt(bone, _mapOrigin + new Vector2(point.X, point.Y) * _mapSize);
            }
        }
        finally { _addingCustomPoint = false; }
    }

    private void HandleEditorCanvas(bool hovered)
    {
        if (_editDefault || _draft is not { } draft) return;
        if (hovered && _hoveredDotIndex >= 0 && _hoveredDotIndex < _drawnPoints.Count)
        {
            var point = _drawnPoints[_hoveredDotIndex];
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _dragPoint = point;
                _dragOffset = ImGui.GetMousePos() - _dotCandidates[_hoveredDotIndex].Pos;
            }
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _contextPoint = point;
                Crystarium.FloatingMenu.Dismiss(_editorId + "-point");
                Crystarium.FloatingMenu.Open(_editorId + "-point", ImGui.GetMousePos(), PointMenu(draft, point));
            }
        }
        if (_dragPoint is { } drag)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _dragPoint = null;
            else if (ImGui.IsMousePosValid() && ImGui.IsMouseHoveringRect(_mapOrigin, _mapOrigin + _mapSize, false))
            {
                var position = (ImGui.GetMousePos() - _dragOffset - _mapOrigin) / _mapSize;
                draft.Move(drag.Bone, drag.Section, position.X, position.Y);
            }
        }
        if (_contextPoint is { } context)
        {
            Crystarium.FloatingMenu.Refresh(_editorId + "-point", PointMenu(draft, context));
            int action = Crystarium.FloatingMenu.Draw(_editorId + "-point");
            if (action == 0) draft.Reset(context.Bone, context.Section, toDefault: false);
            if (action == 1) draft.Reset(context.Bone, context.Section, toDefault: true);
            if (action == 2) draft.Remove(context.Bone);
            if (action == 3 && _editActor is { } actorId && _scene.Snapshot.FindActor(actorId) is { } actor)
                draft.PlaceMirror(context.Bone, context.Section, AvailableBones(actor), SectionCenter(context.Section));
            if (action == 4 && _editActor is { } owner && _scene.Snapshot.FindActor(owner) is { } target)
                draft.RemoveWithChildren(context.Bone, AvailableBones(target));
            if (action == 5) draft.Add(context.Bone);
        }
    }

    private ContextMenuItem[] PointMenu(BoneMapPresetDraft draft, (PortableBoneId Bone, string Section) point)
    {
        var actor = _editActor is { } id ? _scene.Snapshot.FindActor(id) : null;
        var mirror = actor == null ? null : draft.PreviewMirror(point.Bone, point.Section,
            AvailableBones(actor), SectionCenter(point.Section));
        return
        [
            new("Reset position", TablerIcon.ArrowBackUp, disabled: !draft.CanReset(point.Bone, point.Section, false)),
            new("Move to default location", TablerIcon.ArrowBackUp, disabled: !draft.CanReset(point.Bone, point.Section, true)),
            new("Remove", TablerIcon.Trash, disabled: !draft.Contains(point.Bone)),
            new(mirror != null && draft.Points.Any(item => item.Bone == mirror.Bone && item.Section == mirror.Section)
                ? "Place mirror opposite" : "Add mirror opposite", TablerIcon.SelectMirror, disabled: mirror == null,
                help: "Reflect across the mapped parent axis, or the panel center when no parent is mapped. Requires an available mirror and space inside the map."),
            new("Remove with children", TablerIcon.Trash, help: "Remove this bone and all descendants from the map, not the actor."),
            new("Add to map", TablerIcon.Plus, disabled: draft.Contains(point.Bone)),
        ];
    }

    private static float SectionCenter(string section) => section switch
    {
        "body" => 337f / 2054f,
        "armor" => 1064f / 2054f,
        "hands" or "tail" or "ivcs_toes" => 1754f / 2054f,
        _ => 0.5f,
    };
}
