using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Overlays;
using Poser.Game.Scene;
using Poser.Services;

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
    private readonly IEntityBindings _bindings;
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly IActorSpawnService _spawns;

    /// <summary>The selection the removal was armed against. The arm is only
    /// live while the selection is still that exact ordered set.</summary>
    private SelectionId[] _armed = Array.Empty<SelectionId>();

    private Action? _pending;

    public SelectionSection(
        SceneSession scene,
        IEntityBindings bindings,
        ISceneLifecycleHistory lifecycle,
        IActorSpawnService spawns)
    {
        _scene = scene;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _spawns = spawns;
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
                    () => _pending = () => SetVisible(group, true),
                    help: $"Show every one of the {group.Count} selected {group.Noun}");
                actions.Button(
                    "Hide all",
                    () => _pending = () => SetVisible(group, false),
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
                    _armed = System.Linq.Enumerable.ToArray(selected);
                    return;
                }
                _pending = () => Remove(group);
            },
            variant: ButtonVariant.Danger,
            help: armed
                ? $"Press again to destroy all {group.Count} selected {group.Noun}."
                : $"Destroy all {group.Count} selected {group.Noun}. "
                    + "Press once to arm, again to confirm."));

        if (armed)
        {
            form.Status(
                group.Actors.Count > 0
                    ? "Removing actors cannot be undone."
                    : "Undo restores everything this removes in one step.");
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

    private void SetVisible(ResolvedGroup group, bool visible)
    {
        foreach (var actor in group.Actors)
            _spawns.SetVisibility(actor, visible);
        foreach (var prop in group.Props)
            prop.Visible = visible;
        foreach (var light in group.Lights)
            light.IsOn = visible;
        foreach (var overlay in group.Overlays)
            overlay.Visible = visible;
    }

    private void Remove(ResolvedGroup group)
    {
        _lifecycle.DestroySelection(
            group.Actors,
            System.Linq.Enumerable.ToList<object>(group.Props),
            group.Lights,
            group.Cameras,
            System.Linq.Enumerable.ToList<object>(group.Overlays));
        _armed = Array.Empty<SelectionId>();
        _scene.Selection.Clear();
    }

    /// <summary>Every LIVE entity behind the selection, by kind. A selection is
    /// homogeneous by construction, so at most one list is ever populated; the
    /// shape carries them all because the group verbs must not depend on that
    /// staying true. Ids that no longer resolve are dropped — a set the scene
    /// has moved past is smaller, not a refusal.</summary>
    private readonly record struct ResolvedGroup(
        IReadOnlyList<IActor> Actors,
        IReadOnlyList<IPropHandle> Props,
        IReadOnlyList<ILight> Lights,
        IReadOnlyList<IVirtualCamera> Cameras,
        IReadOnlyList<IOverlayNode> Overlays)
    {
        public int Count =>
            Actors.Count + Props.Count + Lights.Count +
            Cameras.Count + Overlays.Count;

        /// <summary>Cameras are the one kind with nothing to show or hide.
        /// </summary>
        public bool CanChangeVisibility => Cameras.Count == 0;

        public string Noun =>
            Actors.Count > 0 ? "actors"
            : Props.Count > 0 ? "objects"
            : Lights.Count > 0 ? "lights"
            : Cameras.Count > 0 ? "cameras"
            : Overlays.Count > 0 ? "overlay nodes"
            : "entities";
    }

    private ResolvedGroup Resolve(IReadOnlyList<SelectionId> selected)
    {
        var actors = new List<IActor>();
        var props = new List<IPropHandle>();
        var lights = new List<ILight>();
        var cameras = new List<IVirtualCamera>();
        var overlays = new List<IOverlayNode>();

        foreach (var id in selected)
        {
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }
                    when _bindings.Resolve(actorId) is
                        { Success: true, Value: { } actor }:
                    actors.Add(actor);
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }
                    when _bindings.Resolve(propId) is
                        { Success: true, Value: { IsValid: true } prop }:
                    props.Add(prop);
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }
                    when _bindings.Resolve(lightId) is
                        { Success: true, Value: { IsValid: true } light }:
                    lights.Add(light);
                    break;
                case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }
                    when _bindings.Resolve(cameraId) is
                        { Success: true, Value: { IsValid: true } camera }:
                    cameras.Add(camera);
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }
                    when _bindings.Resolve(overlayId) is
                        { Success: true, Value: { IsValid: true } overlay }:
                    overlays.Add(overlay);
                    break;
            }
        }

        return new ResolvedGroup(actors, props, lights, cameras, overlays);
    }
}
