using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Selection;

/// <summary>A pinned subject with shared workspace selection, or a live editor.</summary>
public sealed class PropertiesContext : IDisposable
{
    private readonly SceneSession _scene;
    private readonly SelectionScope? _targets;
    private readonly bool _followWorkspaceBones = true;
    /// <summary>Effective command targets: the pinned subject, or its globally selected bones.</summary>
    public SelectionScope Selection { get; }
    /// <summary>The only cursor for selection actions and viewport highlights.</summary>
    public SelectionScope WorkspaceSelection => _scene.Selection.Live;
    public bool IsPinned { get; }

    // A pin has no selection cursor to clear; clearing the workspace here
    // would affect an unrelated subject while leaving the pin unchanged.
    public bool CanDeselectGroup => !IsPinned;
    public void DeselectGroup()
    {
        if (CanDeselectGroup) WorkspaceSelection.Clear();
    }

    public PropertiesContext(SceneSession scene)
    {
        _scene = scene;
        Selection = scene.Selection.Live;
    }

    private PropertiesContext(SceneSession scene, IReadOnlyList<SelectionId> targets, bool followWorkspaceBones = true)
    {
        _scene = scene;
        IsPinned = true;
        _followWorkspaceBones = followWorkspaceBones;
        Selection = new SelectionScope(() => { });
        _targets = new SelectionScope(() => { });
        foreach (var target in targets)
            _targets.Add(target.OwningActor is { } actor ? SelectionId.ForActor(actor) : target);
        RefreshSelection(WorkspaceSelection.Selected);
        _scene.SceneChanged += Refresh;
        _scene.Selection.SelectionChanged += RefreshSelection;
    }

    public PropertiesContext Pin() => new(_scene, (_targets ?? Selection).Selected.ToArray());

    /// <summary>An entity-only inspector pin; actor roots never follow bone selection.</summary>
    public PropertiesContext PinEntities() => new(_scene, (_targets ?? Selection).Selected.ToArray(), false);

    public bool IsAvailable => (_targets ?? Selection).Selected.Count > 0 &&
        (_targets ?? Selection).Selected.All(id => Resolve(id) == id);

    private void Refresh(SceneSnapshot snapshot)
    {
        // Keep a missing logical target, rather than replacing it with another
        // selected object. Undo may publish a new generation of the same target.
        _targets!.Reconcile(id => Resolve(id) ?? id);
        RefreshSelection(WorkspaceSelection.Selected);
    }

    private void RefreshSelection(IReadOnlyList<SelectionId> selected)
    {
        var targets = _targets!.Selected;
        // Pin the actor, not a second bone cursor. Another actor's selection
        // must never retarget this editor or leak into its commands.
        var next = _followWorkspaceBones && targets.Count == 1 && targets[0].Actor is { } actor &&
            selected.Count > 0 && selected.All(id => id.OwningActor == actor)
                ? selected : targets;
        if (Selection.Selected.SequenceEqual(next))
            return;
        Selection.Clear();
        foreach (var id in next)
            Selection.Add(id);
    }

    private SelectionId? Resolve(SelectionId id) =>
        _scene.Resolve(id) is { } resolved && resolved.Kind == id.Kind
            ? resolved
            : null;

    public void Dispose()
    {
        if (IsPinned)
        {
            _scene.SceneChanged -= Refresh;
            _scene.Selection.SelectionChanged -= RefreshSelection;
        }
    }
}
