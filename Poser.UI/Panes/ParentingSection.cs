using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

public sealed class ParentingSection(ITransformParenting parenting, SceneSession scene, UserNotices notices,
    Config.ConfigurationService configuration)
{
    private sealed record Choice(SelectionId Id, string Name, string Kind);
    private static readonly string[] Modes = ["None", "Entity", "Bone"];
    private readonly Crystarium.SearchPicker<Choice> _entities = new("parent-entity");
    private readonly Crystarium.SearchPicker<BoneChoice> _bones = new("parent-bone");
    private Choice[] _entityChoices = [];
    private IReadOnlyList<BoneChoice> _boneChoices = [];
    private SelectionId? _child, _observedTarget, _editing;
    private ActorId? _actor;
    private int _mode;
    public Func<ActorDescriptor, IReadOnlyList<BoneChoice>>? BuildBoneChoices { get; set; }
    public bool Supports(SelectionId id) => parenting.CanParent(id);

    private IEnumerable<Choice> Entities()
    {
        foreach (var actor in scene.Snapshot.Actors)
            yield return new(SelectionId.ForActor(actor.Id), ActorNames.Display(configuration, actor), "Actor");
        foreach (var light in scene.Snapshot.Lights) yield return new(SelectionId.ForLight(light.Id), light.Name, "Light");
        foreach (var prop in scene.Snapshot.Props) yield return new(SelectionId.ForProp(prop.Id), prop.Name, "Object");
        foreach (var obj in scene.Snapshot.WorldObjects) yield return new(SelectionId.ForWorldObject(obj.Id), obj.Name, "Scenery / VFX");
        foreach (var overlay in scene.Snapshot.Overlays.Where(o => o.Kind == Domain.Presentation.OverlayNodeKind.Collider))
            yield return new(SelectionId.ForOverlay(overlay.Id), overlay.Name, "Collider");
    }

    private void Apply(SelectionId child, SelectionId? target)
    {
        var result = parenting.Attach(child, target);
        if (!result.Success) notices.Failed("Parent", result.Detail ?? "Parenting failed.");
        else _child = null; // Reconcile from the committed relationship on the next draw.
    }

    public void Draw(Crystarium.FormScope form, SelectionId child)
    {
        var target = parenting.Read(child)?.Target;
        if (_child != child || _observedTarget != target)
        {
            _child = child;
            _observedTarget = target;
            _mode = target == null ? 0 : target.Value.Bone != null ? 2 : 1;
            _actor = target?.Bone?.Skeleton.Actor ?? target?.Actor;
        }
        form.EndPair();
        form.Dropdown("Target", Modes, _mode, next =>
        {
            if (next == 0) Apply(child, null);
            else _mode = next;
        }, help: "Follow an entity or bone while keeping the current position and rotation offset");

        if (_mode == 1)
        {
            string label = target is { Bone: null } entity
                ? Entities().FirstOrDefault(c => c.Id == entity)?.Name ?? "Target unavailable"
                : "Choose an entity";
            form.Picker("Entity", label, () =>
            {
                _editing = child;
                _entityChoices = Entities().Where(c => c.Id != child).ToArray();
                var options = new PickerOptions<Choice>
                {
                    Query = query => _entityChoices.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || c.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    Badge = c => c.Kind,
                };
                _entities.Open("parent-entity", _entityChoices, c => c.Name, c => c.Id.ToString(), options: in options);
            }, help: label);
        }
        else if (_mode == 2)
        {
            var actors = scene.Snapshot.Actors;
            var names = new[] { "Any actor" }.Concat(actors.Select(a => ActorNames.Display(configuration, a))).ToArray();
            int index = -1;
            for (int i = 0; i < actors.Count; i++) if (actors[i].Id == _actor) index = i;
            form.Dropdown("Actor", names, index + 1, next => _actor = next == 0 ? null : actors[next - 1].Id,
                help: "Choose whose bones to browse, or pick a bone directly in the view");
            var actor = actors.FirstOrDefault(a => a.Id == _actor);
            var selectedBone = target?.Bone is { } bone && bone.Skeleton.Actor == _actor ? bone : (BoneId?)null;
            string label = selectedBone is { } selected
                ? actor?.Skeletons.SelectMany(s => s.Bones).FirstOrDefault(b => b.Id == selected)?.DisplayName ?? selected.CanonicalName
                : "Choose a bone";
            form.Picker("Bone", label, () =>
            {
                if (actor == null || BuildBoneChoices == null) return;
                _editing = child;
                _boneChoices = BuildBoneChoices(actor);
                var options = new PickerOptions<BoneChoice>
                {
                    Query = query => _boneChoices.Where(c => c.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    Badge = c => c.Badge,
                };
                _bones.Open("parent-bone", _boneChoices, c => c.Label, c => c.Key, options: in options);
            }, disabled: actor == null || BuildBoneChoices == null, help: selectedBone?.CanonicalName ?? "Choose a bone in the separate bone list");
        }

        if (_mode != 0 || target != null)
            form.Actions(string.Empty, actions =>
            {
                if (_mode == 2)
                    actions.IconButton(TablerIcon.Crosshair, () => Controls.BonePick.Begin(false,
                        bone => Apply(child, SelectionId.ForBone(bone)),
                        onlyActor: scene.Snapshot.Actors.FirstOrDefault(a => a.Id == _actor)?.Id),
                        help: "Pick the parent bone in the view");
                actions.Button("Detach", () => Apply(child, null), disabled: target == null,
                    help: "Stop following and keep the current world placement");
            });

        if (_entities.Draw() is { } entityPick && _editing is { } entityChild) Apply(entityChild, entityPick.Item.Id);
        if (_bones.Draw() is { } bonePick && _editing is { } boneChild) Apply(boneChild, SelectionId.ForBone(bonePick.Item.BoneId));
    }
}
