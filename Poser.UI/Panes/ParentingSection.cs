using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.UI;

public sealed class ParentingSection(ITransformParenting parenting, SceneSession scene, UserNotices notices)
{
    private sealed record Choice(SelectionId Id, string Name);
    private readonly Crystarium.SearchPicker<Choice> _picker = new("entity-parent");
    private Choice[] _choices = [];
    private SelectionId? _editing;
    public bool Supports(SelectionId id) => parenting.CanParent(id);

    private IEnumerable<Choice> Choices()
    {
        foreach (var actor in scene.Snapshot.Actors)
        {
            yield return new(SelectionId.ForActor(actor.Id), actor.Name);
            foreach (var skeleton in actor.Skeletons)
                foreach (var bone in skeleton.Bones.Where(b => !b.IsHidden))
                    yield return new(SelectionId.ForBone(bone.Id), $"{actor.Name} / {skeleton.Slot} / {bone.DisplayName} ({bone.Id.CanonicalName})");
        }
        foreach (var light in scene.Snapshot.Lights) yield return new(SelectionId.ForLight(light.Id), light.Name);
        foreach (var prop in scene.Snapshot.Props) yield return new(SelectionId.ForProp(prop.Id), prop.Name);
        foreach (var obj in scene.Snapshot.WorldObjects) yield return new(SelectionId.ForWorldObject(obj.Id), obj.Name);
        foreach (var overlay in scene.Snapshot.Overlays.Where(o => o.Kind == Domain.Presentation.OverlayNodeKind.Collider))
            yield return new(SelectionId.ForOverlay(overlay.Id), overlay.Name);
    }

    private void Apply(SelectionId child, SelectionId? target)
    {
        var result = parenting.Attach(child, target);
        if (!result.Success) notices.Failed("Parent", result.Detail ?? "Parenting failed.");
    }

    public void Draw(Crystarium.FormScope form, SelectionId child)
    {
        var current = parenting.Read(child);
        string label = current == null ? "None" : Choices().FirstOrDefault(c => c.Id == current.Target)?.Name ?? "Parent unavailable";
        form.Actions("Parent", actions =>
        {
            actions.Button(label, () =>
            {
                _editing = child;
                _choices = Choices().Where(c => c.Id != child).ToArray();
                var options = new PickerOptions<Choice>
                {
                    Query = query => _choices.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(),
                };
                _picker.Open("parent", _choices, c => c.Name, c => c.Id.ToString(), options: in options);
            }, help: "Follow an entity or bone while preserving the current position and rotation offset");
            actions.IconButton(TablerIcon.Crosshair, () => Controls.BonePick.Begin(false,
                bone => Apply(child, SelectionId.ForBone(bone))), help: "Pick a parent bone in the view");
            actions.Button("Detach", () => Apply(child, null), disabled: current == null,
                help: "Stop following and keep the current world placement");
        });
        if (_picker.Draw() is { } picked && _editing is { } editing) Apply(editing, picked.Item.Id);
    }
}
