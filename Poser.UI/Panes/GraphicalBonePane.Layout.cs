using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Documents.Config;

namespace Poser.UI;

public sealed partial class GraphicalBonePane
{
    private readonly Dictionary<ActorId, (SkeletonDescriptor[] Skeletons,
        IReadOnlyDictionary<PortableBoneId, BoneDescriptor> Bones)> _availableCache = new();

    private IReadOnlyDictionary<PortableBoneId, BoneDescriptor> AvailableBones(ActorDescriptor actor)
    {
        if (_availableCache.TryGetValue(actor.Id, out var cached) && cached.Skeletons.Length == actor.Skeletons.Count)
        {
            bool unchanged = true;
            for (int i = 0; i < cached.Skeletons.Length; i++)
                if (!ReferenceEquals(cached.Skeletons[i], actor.Skeletons[i])) { unchanged = false; break; }
            if (unchanged) return cached.Bones;
        }
        // A pane needs only its displayed actor and its independently pinned editor actor.
        if (_availableCache.Count > 2) _availableCache.Clear();
        var bones = BoneMapPresetDraft.Available(actor);
        _availableCache[actor.Id] = (actor.Skeletons.ToArray(), bones);
        return bones;
    }

    private List<BoneMapPoint> BuildDefaults(ActorDescriptor actor, BoneMapKind kind, string? template = null)
    {
        var points = new List<BoneMapPoint>();
        if (actor.CharacterSkeleton is not { } skeleton) return points;
        var available = AvailableBones(actor);
        void Add(BoneDescriptor? bone, string section, Vector2 position)
        {
            if (bone != null && available.ContainsKey(PortableBoneId.From(bone.Id)))
                points.Add(new(PortableBoneId.From(bone.Id), section, position.X, position.Y));
        }
        void Section(string name, Vector4 rect, bool mirrors)
        {
            if (!_config.PoseImages.TryGetValue(name, out var section)
                || section.Image == null || !_imageSizes.TryGetValue(section.Image, out var source)) return;
            string panel = kind == BoneMapKind.Face ? "face" : name;
            foreach (var entry in section.Bones)
            {
                Vector2 At(Vector2 p) => new Vector2(rect.X, rect.Y) + p / source * new Vector2(rect.Z, rect.W);
                Add(FindBone(skeleton, entry.Name), panel, At(entry.PositionVector));
                if (mirrors && GetMirrorBoneName(entry.Name) is { } mirror)
                    Add(FindBone(skeleton, mirror), panel, At(new(source.X - entry.PositionVector.X, entry.PositionVector.Y)));
            }
        }
        if (kind == BoneMapKind.Face)
            Section(template ?? _customizeRead.HeadSectionFor(actor.Id), new(0, 0, 1, 1), true);
        else
        {
            Vector4 Rect(float x, float y, float w, float h) => new(x / 2054f, y / 1147f, w / 2054f, h / 1147f);
            Section("body", Rect(0, 0, 674, 1147), true);
            Add(FindBone(skeleton, "n_root"), "body", new(337f / 2054f, 1105f / 1147f));
            Section("armor", Rect(714, 0, 700, 1147), true);
            Section("hands", Rect(1454, 0, 600, 427), true);
            Section("tail", Rect(1529, 447, 450, 464), false);
            Section("ivcs_toes", Rect(1454, 931, 600, 216), true);
        }
        return points.DistinctBy(point => (point.Bone, point.Section)).ToList();
    }

    private ActorId? _presetActor;
    private readonly Dictionary<ActorId, (object Bones, BoneMapPreset[] Presets)> _templateCache = new();

    private BoneMapPreset[] EditorPresets(ActorDescriptor actor, BoneMapKind kind)
    {
        var bones = AvailableBones(actor);
        if (!_templateCache.TryGetValue(actor.Id, out var cache) || !ReferenceEquals(cache.Bones, bones))
        {
            if (_templateCache.Count > 2) _templateCache.Clear();
            cache = (bones, BoneMapTemplates.All.Select(template => new BoneMapPreset
            {
                Id = template.Id, Kind = template.Kind, Name = template.Name, Template = template.Section,
                Points = BuildDefaults(actor, template.Kind, template.Section),
            }).ToArray());
            _templateCache[actor.Id] = cache;
        }
        return cache.Presets.Where(item => item.Kind == kind)
            .Concat(_configuration.Config.Skeleton.BoneMapPresets.Where(item => item.Kind == kind)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private BoneMapPreset? SelectedPreset(int page, ActorDescriptor? actor = null)
    {
        actor ??= GetSelectedActor();
        if (actor == null) return null;
        if (_presetActor != actor.Id)
        {
            _presetActor = actor.Id;
            Array.Clear(_selectedPresets);
        }
        var presets = EditorPresets(actor, (BoneMapKind)page);
        var selected = presets.FirstOrDefault(item => item.Id == _selectedPresets[page]);
        if (selected != null) return selected;
        var profile = _customizeRead.MapProfileFor(actor.Id);
        var id = BoneMapTemplates.ResolveDefault(_configuration.Config.Skeleton, (BoneMapKind)page,
            profile.Race, profile.Gender, _customizeRead.HeadSectionFor(actor.Id));
        return presets.First(item => item.Id == id);
    }

    public ContextMenuItem[] AddToPresetActions(BoneId bone)
    {
        if (_scene.Snapshot.FindActor(bone.Skeleton.Actor) is not { } actor || !IsHumanoid(actor.Id)) return [];
        if (!AvailableBones(actor).TryGetValue(PortableBoneId.From(bone), out var currentBone) || currentBone.Id != bone) return [];
        var actions = new List<ContextMenuItem>();
        for (int page = 0; page < 2; page++)
        {
            // The sidebar is shared; the active draft may belong to a pinned Properties host.
            var owner = EditingMap(actor.Id, (BoneMapKind)page, null) ? this
                : _editors.Find(actor.Id, (BoneMapKind)page) ?? this;
            if (owner.SelectedPreset(page, actor) is not { } preset) continue;
            var key = PortableBoneId.From(bone);
            var available = AvailableBones(actor);
            var draft = MatchingEditor(actor.Id, preset) ?? new(preset.Kind,
                BuildDefaults(actor, preset.Kind, preset.Template), preset);
            var preview = new BoneMapPresetDraft(preset.Kind, BuildDefaults(actor, preset.Kind, preset.Template), initial: draft.Points);
            AddPresetBone(preview, key, available);
            string section = preview.Points.First(point => point.Bone == key).Section;
            var mirror = preview.PreviewMirror(key, section, available, SectionCenter(section));
            string help = BoneMapTemplates.IsBuiltIn(preset.Id)
                ? "Creates and selects an editable copy of this locked template."
                : "Updates this map immediately; changes in an open editor remain unsaved until Save or Confirm.";
            actions.Add(new(preset.Kind.ToString(), TablerIcon.Edit, help: help, submenuItems:
            [
                Action("Add", 0, draft.Contains(key)),
                Action("Add with mirror", 1, mirror == null || (draft.Contains(key) && draft.Contains(mirror.Bone))),
                Action("Remove", 2, !draft.Contains(key)),
                Action("Remove with children", 3, false),
            ]));

            ContextMenuItem Action(string label, int operation, bool disabled) => new(label,
                operation >= 2 ? TablerIcon.Trash : operation == 1 ? TablerIcon.SelectMirror : TablerIcon.Plus,
                disabled: disabled, keepOpen: true, help: help)
            {
                OnInvoke = () => owner.ApplyPresetAction(bone, preset.Id, preset.Kind, operation),
            };
        }
        return actions.ToArray();
    }

    private BoneMapPresetDraft? MatchingEditor(ActorId actor, BoneMapPreset preset) =>
        BoneMapTemplates.IsBuiltIn(preset.Id) ? null
            : EditingMap(actor, preset.Kind, preset.Id) ? _draft
            : _editors.Find(actor, preset.Kind, preset.Id)?._draft;

    private void AddPresetBone(BoneMapPresetDraft draft, PortableBoneId key,
        IReadOnlyDictionary<PortableBoneId, BoneDescriptor> available)
    {
        if (draft.Contains(key)) return;
        draft.Add(key);
        // Factory positions win. New auxiliary bones start beside their mapped parent,
        // or the correct panel center, so a new pair fits without changing existing points.
        var point = draft.Points.First(item => item.Bone == key);
        if (point.X != .5f || point.Y != .5f) return;
        var parent = available.TryGetValue(key, out var bone) && bone.Parent is { } id
            ? draft.Points.FirstOrDefault(item => item.Bone == PortableBoneId.From(id) && item.Section == point.Section) : null;
        float x = parent?.X ?? SectionCenter(point.Section);
        if (GetMirrorBoneName(key.CanonicalName) != null) x += key.CanonicalName.EndsWith("_l", StringComparison.Ordinal) ? -.03f : .03f;
        draft.Move(key, point.Section, x, parent == null ? .5f : parent.Y + .04f);
    }

    private void ApplyPresetAction(BoneId bone, Guid presetId, BoneMapKind kind, int operation)
    {
        if (_scene.Snapshot.FindActor(bone.Skeleton.Actor) is not { } actor || !IsHumanoid(actor.Id)) return;
        var available = AvailableBones(actor);
        var key = PortableBoneId.From(bone);
        if (!available.TryGetValue(key, out var resolved) || resolved.Id != bone) return;
        var preset = EditorPresets(actor, kind).FirstOrDefault(item => item.Id == presetId);
        if (preset == null) return;
        var store = _configuration.Config.Skeleton.BoneMapPresets;
        var editor = MatchingEditor(actor.Id, preset);
        bool locked = BoneMapTemplates.IsBuiltIn(preset.Id);
        var draft = editor ?? (locked
            ? new BoneMapPresetDraft(kind, BuildDefaults(actor, kind, preset.Template), initial: preset.Points)
                { Name = BoneMapPresetDraft.UniqueName(store, kind, preset.Name + " copy"), Template = preset.Template, Background = preset.Background }
            : new BoneMapPresetDraft(kind, BuildDefaults(actor, kind, preset.Template), preset));
        var before = draft.Points.ToArray();
        if (operation is 0 or 1) AddPresetBone(draft, key, available);
        if (operation == 1)
        {
            var point = draft.Points.First(item => item.Bone == key);
            var mirror = draft.PreviewMirror(key, point.Section, available, SectionCenter(point.Section));
            if (mirror != null && !draft.Contains(mirror.Bone))
                draft.PlaceMirror(key, point.Section, available, SectionCenter(point.Section));
        }
        if (operation == 2) draft.Remove(key);
        if (operation == 3) draft.RemoveWithChildren(key, available);
        if (before.SequenceEqual(draft.Points) || editor != null) return;
        _editError = draft.Save(store, out var saved);
        if (_editError != null) return;
        _selectedPresets[(int)kind] = saved;
        _configuration.Save();
        if (_editActor == actor.Id && _draft?.SourceId == presetId)
            LoadEditorPreset(store.First(item => item.Id == saved));
    }
}
