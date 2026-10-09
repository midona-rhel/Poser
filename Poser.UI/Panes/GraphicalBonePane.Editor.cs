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
    private List<BoneMapPoint> _editorDefaults = new();
    private ActorId? _editActor;
    private string _editFilter = string.Empty;
    private string? _editError;
    private readonly string _editorId = "bone-map-editor-" + Guid.NewGuid().ToString("N");
    private (PortableBoneId Bone, string Section)? _dragPoint, _contextPoint;
    private Vector2 _dragOffset;
    private readonly object _editorHoverOwner = new();
    private BoneMapPreset? SelectedPreset(int page) => _configuration.Config.Skeleton.BoneMapPresets
        .FirstOrDefault(item => item.Id == _selectedPresets[page] && (int)item.Kind == page);

    private void DrawPresetActions(int page, ActorDescriptor actor, Vector2 origin, float width)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var presets = _configuration.Config.Skeleton.BoneMapPresets
            .Where(item => (int)item.Kind == page).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new[] { "Default" }.Concat(presets.Select(item => item.Name)).ToArray();
        int selected = Array.FindIndex(presets, item => item.Id == _selectedPresets[page]) + 1;
        ImGui.SetCursorScreenPos(origin + new Vector2(width - 200f * scale, 0f));
        Crystarium.Dropdown("##map-preset", names, selected,
            index => _selectedPresets[page] = index == 0 ? Guid.Empty : presets[index - 1].Id,
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
            _editorDefaults = BuildDefaults(editingActor, (BoneMapKind)opening);
            var preset = SelectedPreset(opening);
            _editDefault = preset == null;
            _draft = new((BoneMapKind)opening, _editorDefaults, preset);
            _editFilter = string.Empty;
            _editError = null;
        }
        if (_draft == null) return;
        var actor = _editActor is { } id ? _scene.Snapshot.FindActor(id) : null;
        if (actor != null && !IsHumanoid(actor.Id)) actor = null;
        Crystarium.Dialog(_editorId, true, open => { if (!open) CloseEditor(); },
            $"Edit {_draft.Kind} map",
            () => DrawEditorBody(actor),
            footer: () =>
            {
                if (Crystarium.Button("Cancel", id: "cancel-map")) CloseEditor();
                ImGui.SameLine();
                if (Crystarium.Button("Save preset", variant: ButtonVariant.Primary,
                    disabled: _editDefault || actor == null || string.IsNullOrWhiteSpace(_draft?.Name), id: "save-map")
                    && _draft is { } draft)
                {
                    _editError = draft.Save(_configuration.Config.Skeleton.BoneMapPresets, out var saved);
                    if (_editError == null)
                    {
                        _selectedPresets[(int)draft.Kind] = saved;
                        _configuration.Save();
                        CloseEditor();
                    }
                }
            }, size: DialogSize.Large, height: 670f, logicalWidth: 1040f);
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

    private void DrawEditorBody(ActorDescriptor? actor)
    {
        if (_draft is not { } draft) return;
        DrawEditorPresetToolbar();
        draft = _draft!;
        Crystarium.TextInput("##map-preset-name", _editDefault ? "Default (locked)" : draft.Name, value => draft.Name = value,
            placeholder: "Preset name", disabled: _editDefault);
        if (actor == null)
        {
            Crystarium.Text("The original actor is no longer available. Close and reopen the editor.");
            return;
        }
        float s = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail();
        float left = 260f * s;
        float height = MathF.Max(100f * s, available.Y - 28f * s);
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
                ImGui.PushID(bone.Id.ToString());
                Crystarium.Checkbox("include", draft.Contains(key), on =>
                { if (on) draft.Add(key); else draft.Remove(key); }, disabled: _editDefault);
                var nameOrigin = row + new Vector2(24f * s, 0f);
                float nameWidth = scroll.ContentWidth * s - 24f * s;
                Crystarium.TextInBand(nameOrigin, new Vector2(nameWidth, 26f * s), bone.DisplayName,
                    default, TextConstraint.Truncate(nameWidth));
                if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(nameOrigin, nameOrigin + new Vector2(nameWidth, 26f * s)))
                    Crystarium.HoverHelp.Preview("bone-name", nameOrigin, nameOrigin + new Vector2(nameWidth, 26f * s),
                        $"{bone.DisplayName} · {bone.Id.CanonicalName} · {bone.Id.Slot}/{bone.Id.PartialId}");
                bool duplicate = duplicates.Contains(bone.DisplayName);
                if (duplicate)
                    Crystarium.TextInBand(nameOrigin + new Vector2(0, 22f * s), new Vector2(nameWidth, 18f * s),
                        $"{bone.Id.CanonicalName} · {bone.Id.Slot}/{bone.Id.PartialId}",
                        new TextStyle { Size = Crystarium.ActiveTheme.Typography.CaptionSize, Color = Crystarium.ActiveTheme.FormHint },
                        TextConstraint.Truncate(nameWidth));
                ImGui.PopID();
                ImGui.SetCursorScreenPos(row + new Vector2(0f, (duplicate ? 44f : 28f) * s));
            }
        });
        ImGui.SetCursorScreenPos(origin + new Vector2(left + 12f * s, 0f));
        var canvas = new Vector2(MathF.Max(1f, available.X - left - 12f * s), height);
        ImGui.BeginChild("##map-preview", canvas, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try { DrawMap((int)draft.Kind, canvas, actor); }
        finally { ImGui.EndChild(); }
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height + 4f * s));
        Crystarium.Text(_editError ?? (_editDefault ? "Default is locked. Choose New to create an editable copy."
            : "Drag points to arrange them. Right-click for reset and remove."));
    }

    private void DrawEditorPresetToolbar()
    {
        if (_draft is not { } draft) return;
        var presets = _configuration.Config.Skeleton.BoneMapPresets
            .Where(item => item.Kind == draft.Kind).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] names = new[] { "Default (locked)" }.Concat(presets.Select(item => item.Name)).ToArray();
        int selected = Array.FindIndex(presets, item => item.Id == draft.SourceId) + 1;
        bool dirty = !_editDefault && draft.HasChanges;
        var row = ImGui.GetCursorScreenPos();
        float s = ImGuiHelpers.GlobalScale;
        float width = ImGui.GetContentRegionAvail().X;
        string preview = _editDefault ? "Default (locked)" : draft.SourceId == null ? "New preset" : draft.Name;
        Crystarium.ActionDropdown("##edit-map-preset", names, selected, preview,
            index => LoadEditorPreset(index == 0 ? null : presets[index - 1]),
            ControlStyle.Workspace with { Width = UiWidth.Region(MathF.Max(120f, width / s - 248f)) }, disabled: dirty,
            help: dirty ? "Save or discard these edits before switching presets" : null);
        ImGui.SetCursorScreenPos(row + new Vector2(width - 236f * s, 0));
        if (Crystarium.Button("New", disabled: dirty, id: "new-map-preset"))
        {
            _draft = new(draft.Kind, _editorDefaults, initial: draft.Points);
            _editDefault = false;
            _editError = null;
            _dragPoint = null;
            _contextPoint = null;
            Crystarium.FloatingMenu.Dismiss(_editorId + "-point");
        }
        ImGui.SetCursorScreenPos(row + new Vector2(width - 173f * s, 0));
        if (Crystarium.Button("Discard edits", disabled: !dirty, id: "discard-map-edits"))
            LoadEditorPreset(presets.FirstOrDefault(item => item.Id == draft.SourceId));
        ImGui.SetCursorScreenPos(row + new Vector2(width - 32f * s, 0));
        if (Crystarium.IconButton(TablerIcon.Trash, disabled: _editDefault || dirty || draft.SourceId == null,
            id: "delete-map-preset", help: "Delete this custom map preset"))
        {
            _editError = draft.Delete(_configuration.Config.Skeleton.BoneMapPresets);
            if (_editError == null)
            {
                _configuration.Save();
                LoadEditorPreset(null);
            }
        }
        ImGui.SetCursorScreenPos(row + new Vector2(0, 36f * s));
    }

    private void LoadEditorPreset(BoneMapPreset? preset)
    {
        if (_draft is not { } draft) return;
        _editDefault = preset == null;
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
            else if (ImGui.IsMousePosValid())
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
        }
    }

    private static ContextMenuItem[] PointMenu(BoneMapPresetDraft draft, (PortableBoneId Bone, string Section) point) =>
    [
        new("Reset position", TablerIcon.ArrowBackUp, disabled: !draft.CanReset(point.Bone, point.Section, false)),
        new("Move to default location", TablerIcon.ArrowBackUp, disabled: !draft.CanReset(point.Bone, point.Section, true)),
        new("Remove", TablerIcon.Trash),
    ];
}
