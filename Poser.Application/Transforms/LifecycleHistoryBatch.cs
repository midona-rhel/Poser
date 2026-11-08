using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>Combines a command affecting several entities into one undo action.
/// Remembers completed steps so another attempt resumes where the previous one stopped.</summary>
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

    /// <summary>Runs this step first on redo and last on undo, after the entities
    /// have been recreated. Build includes it when the batch has entity changes.</summary>
    public void AddPrologue(HistoryEntry entry)
    {
        if (entry is not InverseEntry)
            throw new ArgumentException("A batch prologue must be invertible.", nameof(entry));
        _children.Insert(0, new(entry) { Prologue = true });
    }

    public SceneLifecyclePatch? Build()
    {
        var actions = _children.Where(child => !child.Prologue).ToArray();
        if (actions.Length == 0)
            return null;
        return new(
            actions.Length == 1 ? actions[0].Entry.Description : description,
            () => Run(undo: true), () => Run(undo: false))
        {
            FailureDetail = () => _failure,
            OnRefusal = () => _children.All(child => child.Dropped) ? RefusalAction.DropNow : RefusalAction.Keep,
            ResolveAffectedEntities = ResolveAffectedEntities,
        };
    }

    private IReadOnlyList<SelectionId>? ResolveAffectedEntities()
    {
        var entities = new HashSet<SelectionId>();
        foreach (var child in _children)
        {
            // Undo can recreate entities with new IDs. If a step's entities are
            // unknown, the whole batch uses global undo.
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
            // Keep the step order. Another attempt starts here, or at the next
            // step if this one was permanently removed.
            return false;
        }
        return true;
    }

    private sealed class Child(HistoryEntry entry)
    {
        public HistoryEntry Entry { get; } = entry;
        public bool Prologue { get; init; }
        public bool Undone { get; set; }
        public bool Dropped { get; set; }
        public bool Refused { get; set; }
    }
}
