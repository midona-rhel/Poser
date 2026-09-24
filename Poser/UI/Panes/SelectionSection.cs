using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>
/// What a MULTI-entity selection can be told to do, stated at the head of the
/// inspector rail while more than one entity is selected — Brio's
/// <c>{N} Selected</c> block (<c>UI/Widgets/Core/EntityManagerWidget.cs</c>),
/// which is the only place either reference puts a verb that consumes a
/// selection rather than a row.
///
/// <para>It carries the two group verbs that are NOT already transforms:
/// visibility and removal. The third — group transform — needs no surface of
/// its own, because the rail's own TRANSLATION rows and the world gizmo drive
/// every selected entity already, and a selection of more than one turns about
/// its own middle (<see cref="Poser.Domain.Transforms.PivotMode.Centroid"/>).
/// </para>
///
/// <para>Removal ARMS before it fires, the same two-press gate every other
/// irreversible act in Poser uses, and the arm dies the moment the selection
/// changes underneath it — a confirm must always be about the set the user was
/// looking at when they armed it. Both verbs run AFTER the page has drawn:
/// destroying an entity republishes the scene, and doing that mid-walk rebuilds
/// the rows being walked.</para>
/// </summary>
public sealed class SelectionSection
{
    private readonly SceneSession _scene;
    private readonly SelectionEntityCommands _entityCommands;
    private readonly UserNotices _notices;

    /// <summary>The selection the removal was armed against. The arm is only
    /// live while the selection is still that exact ordered set.</summary>
    private SelectionId[] _armed = Array.Empty<SelectionId>();

    private Action? _pending;

    public SelectionSection(
        SceneSession scene,
        SelectionEntityCommands entityCommands,
        UserNotices notices)
    {
        _entityCommands = entityCommands;
        _scene = scene;
        _notices = notices;
    }

    /// <summary>Draws the section and answers the height it took; zero when
    /// the selection is one entity or none, which is every ordinary frame.
    /// </summary>
    public float Draw(Vector2 origin, float width)
    {
        var selected = _scene.Selection.Selected;
        if (selected.Count < 2)
        {
            _armed = Array.Empty<SelectionId>();
            return 0f;
        }

        var group = Resolve(selected);
        if (group.Count < 2)
        {
            _armed = Array.Empty<SelectionId>();
            return 0f;
        }

        if (!ArmedFor(selected))
            _armed = Array.Empty<SelectionId>();

        float height = Crystarium.Section(
            "selection-group",
            "Selection",
            origin,
            width,
            true,
            null,
            form => Rows(form, selected, group),
            divider: false);

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
        return height;
    }

    private void Rows(
        Crystarium.FormScope form,
        IReadOnlyList<SelectionId> selected,
        ResolvedGroup group)
    {
        form.Status($"{group.Count} {group.Noun} selected.");

        // A camera has nothing to show or hide; every other kind does, and the
        // two verbs are separate rows rather than one toggle because a mixed
        // set has no single state to flip.
        if (group.CanChangeVisibility)
        {
            form.Actions("Visibility", actions =>
            {
                actions.Button(
                    "Show all",
                    () => QueueVisibility(selected, true),
                    help: $"Show every one of the {group.Count} selected {group.Noun}");
                actions.Button(
                    "Hide all",
                    () => QueueVisibility(selected, false),
                    help: $"Hide every one of the {group.Count} selected {group.Noun} "
                        + "without destroying them");
            });
        }

        bool armed = ArmedFor(selected);
        form.Actions("Lifetime", actions => actions.Button(
            armed ? $"Confirm remove {group.Count}" : "Remove",
            () =>
            {
                if (!armed)
                {
                    _armed = selected.ToArray();
                    return;
                }
                var ids = selected.ToArray();
                _pending = () => Remove(ids);
            },
            variant: ButtonVariant.Danger,
            help: armed
                ? $"Press again to destroy all {group.Count} selected {group.Noun}."
                : $"Destroy all {group.Count} selected {group.Noun}. "
                    + "Press once to arm, again to confirm."));

        if (armed && !group.HasActors)
        {
            form.Status("Undo restores everything this removes in one step.");
        }
    }

    private bool ArmedFor(IReadOnlyList<SelectionId> selected)
    {
        if (_armed.Length != selected.Count)
            return false;
        for (int index = 0; index < _armed.Length; index++)
        {
            if (_armed[index] != selected[index])
                return false;
        }
        return true;
    }

    private void QueueVisibility(IReadOnlyList<SelectionId> selected, bool visible)
    {
        var ids = selected.ToArray();
        _pending = () => _entityCommands.SetVisibility(ids, visible);
    }

    private async void Remove(IReadOnlyList<SelectionId> ids)
    {
        _armed = Array.Empty<SelectionId>();
        try
        {
            _notices.Removal(await _entityCommands.Remove(ids));
        }
        catch (Exception exception)
        {
            _notices.Failed("Remove", exception.Message);
        }
    }

    /// <summary>Current pointer-free facts for the selected entities. Commands
    /// recheck these capabilities after drawing, against the captured ids.</summary>
    private readonly record struct ResolvedGroup(
        IReadOnlyList<CurrentSelectionEntity> Entities)
    {
        public int Count => Entities.Count;

        public bool CanChangeVisibility => Entities.Any(entity => entity.CanChangeVisibility);

        public bool HasActors => Entities.Any(entity => entity.Id.Kind == SceneEntityKind.Actor);

        public string Noun =>
            Entities.Select(entity => entity.Id.Kind).Distinct().Count() != 1
                ? "entities"
                : Entities[0].Id.Kind switch
                {
                    SceneEntityKind.Actor => "actors",
                    SceneEntityKind.Prop => "objects",
                    SceneEntityKind.Light => "lights",
                    SceneEntityKind.Camera => "cameras",
                    SceneEntityKind.Overlay => "overlay nodes",
                    SceneEntityKind.WorldObject => "objects",
                    _ => "entities",
                };
    }

    private ResolvedGroup Resolve(IReadOnlyList<SelectionId> selected)
    {
        var entities = new List<CurrentSelectionEntity>();

        foreach (var id in selected)
        {
            if (_scene.ReadCurrent(id) is { } entity && entity.Id == id)
                entities.Add(entity);
        }

        return new ResolvedGroup(entities);
    }
}
