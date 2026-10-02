using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Selection;

/// <summary>An explicit editor target. Pinned editors share commands and scene reads,
/// not the workspace's selection cursor.</summary>
public sealed class PropertiesContext : IDisposable
{
    private readonly SceneSession _scene;
    public SelectionScope Selection { get; }
    public bool IsPinned { get; }

    public PropertiesContext(SceneSession scene)
    {
        _scene = scene;
        Selection = scene.Selection.Live;
    }

    private PropertiesContext(SceneSession scene, IReadOnlyList<SelectionId> targets)
    {
        _scene = scene;
        IsPinned = true;
        Selection = new SelectionScope(() => { });
        foreach (var target in targets)
            Selection.Add(target);
        _scene.SceneChanged += Refresh;
    }

    public PropertiesContext Pin() => new(_scene, Selection.Selected.ToArray());

    public bool IsAvailable => Selection.Selected.Count > 0 &&
        Selection.Selected.All(id => Resolve(id) == id);

    private void Refresh(SceneSnapshot snapshot)
    {
        // Keep a missing logical target, rather than replacing it with another
        // selected object. Undo may publish a new generation of the same target.
        Selection.Reconcile(id => Resolve(id) ?? id);
    }

    private SelectionId? Resolve(SelectionId id) =>
        _scene.Resolve(id) is { } resolved && resolved.Kind == id.Kind
            ? resolved
            : null;

    public void Dispose()
    {
        if (IsPinned)
            _scene.SceneChanged -= Refresh;
    }
}
