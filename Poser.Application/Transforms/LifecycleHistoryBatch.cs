using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>One removal command's already-recorded inverses, with per-child
/// progress so a refused retry never repeats a sibling that already landed.</summary>
internal sealed class LifecycleHistoryBatch(string description)
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly List<Child> _children = new();
    private string? _failure;

    public bool TryAdd(HistoryEntry entry)
    {
        if (Environment.CurrentManagedThreadId != _thread || entry.RequiredAsset is not null || entry is JournalStep { CompleteReplay: not null }
            || entry is not InverseEntry) return false;
        _children.Add(new(entry));
        return true;
    }

    public SceneLifecyclePatch? Build() => _children.Count == 0 ? null : new(
        _children.Count == 1 ? _children[0].Entry.Description : description,
        () => Run(undo: true), () => Run(undo: false))
    {
        FailureDetail = () => _failure,
        OnRefusal = () => _children.All(child => child.Dropped) ? RefusalAction.DropNow : RefusalAction.Keep,
        ResolveAffectedEntities = ResolveAffectedEntities,
    };

    private IReadOnlyList<SelectionId>? ResolveAffectedEntities()
    {
        var entities = new HashSet<SelectionId>();
        foreach (var child in _children)
        {
            // Resolve on each lookup: lifecycle restoration can replace IDs.
            // One unknown child makes the entire batch an ordering barrier.
            var affected = EditHistory.EntitiesOf(child.Entry);
            if (affected is null || affected.Count == 0) return null;
            entities.UnionWith(affected);
        }
        return entities.Count == 0 ? null : entities.ToArray();
    }

    private bool Run(bool undo)
    {
        _failure = null;
        foreach (var child in undo ? _children.AsEnumerable().Reverse() : _children)
        {
            if (child.Dropped || child.Undone == undo) continue;
            var entry = (InverseEntry)child.Entry;
            bool landed;
            string? detail = null;
            try
            {
                landed = undo ? entry.Undo() : entry.Redo();
            }
            catch (Exception ex) { landed = false; detail = ex.Message; }
            if (landed)
            {
                child.Undone = undo;
                child.Refused = false;
                continue;
            }
            detail ??= entry.FailureDetail?.Invoke();
            var action = RefusalPolicy.Decide(entry);
            child.Dropped = action == RefusalAction.DropNow
                || (action == RefusalAction.DropOnRepeat && child.Refused);
            child.Refused = action == RefusalAction.DropOnRepeat;
            _failure = detail ?? $"Could not {(undo ? "undo" : "redo")} {child.Entry.Description.ToLowerInvariant()}.";
            // Stop at the first refusal to preserve inverse ordering. A retry
            // resumes here (or after a permanently discarded child).
            return false;
        }
        return true;
    }

    private sealed class Child(HistoryEntry entry)
    {
        public HistoryEntry Entry { get; } = entry;
        public bool Undone { get; set; }
        public bool Dropped { get; set; }
        public bool Refused { get; set; }
    }
}
