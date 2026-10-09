using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Application.Presentation;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

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

    private List<BoneMapPoint> BuildDefaults(ActorDescriptor actor, BoneMapKind kind)
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
            Section(_customizeRead.HeadSectionFor(actor.Id), new(0, 0, 1, 1), true);
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

    public ContextMenuItem[] AddToPresetActions(BoneId bone)
    {
        if (_scene.Snapshot.FindActor(bone.Skeleton.Actor) is not { } actor || !IsHumanoid(actor.Id)) return [];
        var actions = new List<ContextMenuItem>();
        for (int page = 0; page < 2; page++)
        {
            if (SelectedPreset(page) is not { } preset) continue;
            var key = PortableBoneId.From(bone);
            actions.Add(new($"{preset.Kind}: {preset.Name}", TablerIcon.Edit,
                disabled: _draft != null || preset.Points.Any(point => point.Bone == key),
                help: _draft != null ? "Finish the open map editor first" : null)
            {
                OnInvoke = () =>
                {
                    if (_draft != null || _scene.Snapshot.FindActor(bone.Skeleton.Actor) is not { } current
                        || !IsHumanoid(current.Id) || !AvailableBones(current).ContainsKey(key)) return;
                    _editActor = current.Id;
                    _editorDefaults = BuildDefaults(current, preset.Kind);
                    _draft = new(preset.Kind, _editorDefaults, preset);
                    _draft.Add(key);
                    _editDefault = false;
                    _editFilter = string.Empty;
                    _editError = null;
                },
            });
        }
        return actions.ToArray();
    }
}
