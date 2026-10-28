using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>
/// Stores undo and redo steps for transforms, property edits, and scene changes.
/// The current history limit is applied whenever a step is added.
/// </summary>
public sealed class EditHistory
{
    /// <summary>History depth used when the caller supplies no setting.</summary>
    public const int DefaultCapacity = 500;

    private static readonly Func<int> FixedDefault = static () => DefaultCapacity;

    private readonly Func<int> _capacity;
    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();
    private readonly Dictionary<SelectionId, Func<SelectionId?>> _lifecycleTargets = new();
    private LifecycleHistoryBatch? _batch;

    /// <summary>Records a command such as deleting a group as one undo step.
    /// Finishes pending edits first. The callback must complete its changes
    /// before returning so they belong to this batch.</summary>
    public void RecordLifecycleBatch(string description, Action removals)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (_batch is not null)
            throw new InvalidOperationException("A lifecycle batch is already recording.");
        Appending?.Invoke();
        BeforeAppend?.Invoke();
        var batch = new LifecycleHistoryBatch(description);
        _batch = batch;
        try { removals(); }
        finally
        {
            if (ReferenceEquals(_batch, batch)) FlushBatch();
        }
    }

    /// <summary>Adds a step that runs first on redo and last on undo, such as
    /// restoring a group's layout after its members have been recreated.</summary>
    public void AppendBatchPrologue(HistoryEntry entry)
    {
        if (_batch is not { } batch)
            throw new InvalidOperationException("No lifecycle batch is recording.");
        batch.AddPrologue(entry);
    }

    private void FlushBatch()
    {
        var batch = _batch;
        _batch = null;
        if (batch?.Build() is { } entry) Append(entry);
    }

    /// <summary>Keeps an entity's edits after deletion and connects them to
    /// the replacement entity when undo recreates it.</summary>
    public void RetainLifecycleTarget(TransformTargetId target, Func<TransformTargetId?> current) =>
        RetainLifecycleEntity(target.ToSelectionId(), () => current()?.ToSelectionId());

    public void RetainLifecycleEntity(SelectionId entity, Func<SelectionId?> current) =>
        _lifecycleTargets[entity] = current;

    public SelectionId ResolveLifecycleEntity(SelectionId entity) =>
        _lifecycleTargets.TryGetValue(entity, out var current) ? current() ?? entity : entity;

    public TransformTargetId ResolveLifecycleTarget(TransformTargetId target)
    {
        var entity = ResolveLifecycleEntity(target.ToSelectionId());
        return entity.Bone is { } bone ? TransformTargetId.ForBone(bone)
            : GroupTransformCoordinator.Target(entity) ?? target;
    }

    private void RefreshLifecycleTargets(List<HistoryEntry> stack)
    {
        if (_lifecycleTargets.Count == 0) return;
        TransformTargetId? Resolve(TransformTargetId target) =>
            ResolveLifecycleTarget(target);
        for (int i = 0; i < stack.Count; i++)
        {
            if (stack[i] is not TransformPatch patch) continue;
            if (!patch.Before.Concat(patch.After).Any(state => Resolve(state.Target) != state.Target))
                continue;
            stack[i] = patch with
            {
                Before = patch.Before.Select(state => state with { Target = Resolve(state.Target)!.Value }).ToArray(),
                After = patch.After.Select(state => state with { Target = Resolve(state.Target)!.Value }).ToArray(),
                GroupState = patch.GroupState?.Remap(Resolve, allowReplacement: true),
            };
        }
    }

    internal event Action? BeforeAppend;

    public EditHistory()
        : this(FixedDefault)
    {
    }

    public EditHistory(Func<int> capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? _redo[^1].Description : null;

    /// <summary>Reports the recorded step to listeners such as the action recorder.</summary>
    public event Action<HistoryEntry>? Appended;

    /// <summary>An undo or redo was committed.</summary>
    public event Action? Stepped;

    /// <summary>Lets listeners record pending changes before the next step.</summary>
    public event Action? Appending;

    public void Append(HistoryEntry patch)
    {
        Appending?.Invoke();
        BeforeAppend?.Invoke();
        if (_batch is { } batch)
        {
            if (batch.TryAdd(patch)) return;
            // Finish the batch before recording a step that needs its own replay context.
            FlushBatch();
        }
        int capacity = _capacity();
        if (capacity < 1)
        {
            // Capacity zero disables undo and redo, including the new entry.
            _undo.Clear();
            _redo.Clear();
            RaiseCleared();
        }
        else
        {
            _undo.Add(patch);
            while (_undo.Count > capacity)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
        // History is already updated. Notify each listener even if an earlier one fails.
        if (Appended is { } readers)
            foreach (Action<HistoryEntry> reader in readers.GetInvocationList())
                try
                {
                    reader(patch);
                }
                catch
                {
                    // Continue notifying the remaining listeners.
                }
    }

    public HistoryEntry? PeekUndo(SelectionId? entity = null)
    {
        RefreshLifecycleTargets(_undo);
        return Find(_undo, entity);
    }

    public void CommitUndo(HistoryEntry patch, SelectionId? entity = null)
    {
        if (entity is null ? PeekUndo()?.Id != patch.Id : !_undo.Any(entry => entry.Id == patch.Id))
            throw new InvalidOperationException(
                "Undo history changed before commit.");
        int index = _undo.FindIndex(entry => entry.Id == patch.Id);
        patch = _undo[index];
        _undo.RemoveAt(index);
        _redo.Add(patch);
        Stepped?.Invoke();
    }

    public HistoryEntry? PeekRedo(SelectionId? entity = null)
    {
        RefreshLifecycleTargets(_redo);
        return Find(_redo, entity);
    }

    public void CommitRedo(HistoryEntry patch, SelectionId? entity = null)
    {
        if (entity is null ? PeekRedo()?.Id != patch.Id : !_redo.Any(entry => entry.Id == patch.Id))
            throw new InvalidOperationException(
                "Redo history changed before commit.");
        int index = _redo.FindIndex(entry => entry.Id == patch.Id);
        patch = _redo[index];
        _redo.RemoveAt(index);
        _undo.Add(patch);
        Stepped?.Invoke();
    }

    public static SelectionId EntityOf(SelectionId selected) =>
        selected.OwningActor is { } actor ? SelectionId.ForActor(actor) : selected;

    private HistoryEntry? Find(List<HistoryEntry> stack, SelectionId? entity)
    {
        if (entity is null) return stack.Count > 0 ? stack[^1] : null;
        var selected = EntityOf(entity.Value);
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            var entry = stack[i];
            var affected = EntitiesOf(entry);
            if (affected is null || affected.Count == 0) return null;
            var entities = affected.Select(id => EntityOf(ResolveLifecycleEntity(id))).Distinct().ToArray();
            if (!entities.Contains(selected)) continue;
            // Creating or deleting an entity uses global undo: deletion leaves
            // nothing to select for redo. Edits shared by several entities also
            // stay together in global history.
            return entities.Length == 1 && entry is not SceneLifecyclePatch ? entry : null;
        }
        return null;
    }

    internal static IReadOnlyList<SelectionId>? EntitiesOf(HistoryEntry entry)
    {
        if (entry.ResolveAffectedEntities is { } resolve) return resolve();
        if (entry.AffectedEntities is { } known) return known;
        if (entry is not TransformPatch patch) return null;
        var states = patch.Before.Concat(patch.After).ToArray();
        // A child transform depends on its parent, so their edits keep the global order.
        if (states.Any(state => state.Parent is not null)) return null;
        return states.Select(state => EntityOf(state.Target.ToSelectionId())).Distinct().ToArray();
    }

    /// <summary>
    /// Reconnects transform edits to replacement bones after a redraw.
    /// Edits wait while their targets are loading. If a target is gone for good,
    /// the entire affected step is removed so undo keeps grouped edits together.
    /// Entity creation and deletion keep their own steps for undo and redo.
    /// </summary>
    public void Reconcile(
        Func<Poser.Domain.Identity.TransformTargetId, bool> isCurrent,
        Func<Poser.Domain.Identity.TransformTargetId, Poser.Domain.Identity.TransformTargetId?>? rekey = null,
        Func<Poser.Domain.Identity.TransformTargetId, bool>? isPending = null)
    {
        RefreshLifecycleTargets(_undo);
        RefreshLifecycleTargets(_redo);
        // Find the same bones on the replacement body before deciding which edits to drop.
        void Rekey(List<HistoryEntry> stack)
        {
            if (rekey is null)
                return;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i] is not TransformPatch patch)
                    continue;
                if (patch.Before.All(state => isCurrent(state.Target))
                    && patch.After.All(state => isCurrent(state.Target)))
                {
                    if (patch.AwaitingTargets) stack[i] = patch with { AwaitingTargets = false };
                    continue;
                }
                var before = Remap(patch.Before);
                var after = before is null ? null : Remap(patch.After);
                var group = patch.GroupState?.Remap(target =>
                    isCurrent(target) ? target : rekey(target));
                if (before is not null && after is not null
                    && (patch.GroupState == null || group != null))
                    stack[i] = patch with { Before = before, After = after, GroupState = group, AwaitingTargets = false };
                else
                    stack[i] = patch with { AwaitingTargets = patch.Before.Concat(patch.After)
                        .Any(state => isPending?.Invoke(state.Target) == true) };
            }
            List<TransformTargetState>? Remap(IReadOnlyList<TransformTargetState> states)
            {
                var mapped = new List<TransformTargetState>(states.Count);
                foreach (var state in states)
                {
                    if (isCurrent(state.Target))
                    {
                        mapped.Add(state);
                        continue;
                    }
                    if (rekey(state.Target) is not { } current)
                        return null;
                    mapped.Add(state with { Target = current });
                }
                return mapped;
            }
        }
        bool Stale(HistoryEntry entry)
        {
            if (entry is not TransformPatch patch)
                return false;
            // A grouped edit waits until all replacement bones are ready.
            // Keep already-found replacements while the others finish redrawing.
            bool Gone(TransformTargetState state) => !isCurrent(state.Target)
                && !_lifecycleTargets.ContainsKey(state.Target.ToSelectionId())
                && (!patch.AwaitingTargets || rekey?.Invoke(state.Target) is null)
                && isPending?.Invoke(state.Target) != true;
            return patch.Before.Any(Gone) || patch.After.Any(Gone);
        }
        Rekey(_undo);
        Rekey(_redo);
        _undo.RemoveAll(entry => Stale(entry));
        _redo.RemoveAll(entry => Stale(entry));
    }

    /// <summary>Removes an undo or redo step after its restore has become obsolete.</summary>
    public void Drop(HistoryEntry entry)
    {
        _undo.Remove(entry);
        _redo.Remove(entry);
    }

    /// <summary>Removes transform steps for a permanently removed target.
    /// A grouped step is removed together if it includes that target.</summary>
    public void DropTransformsFor(TransformTargetId target)
    {
        bool Touches(HistoryEntry entry) => entry is TransformPatch patch
            && (patch.Before.Any(state => state.Target == target)
                || patch.After.Any(state => state.Target == target));
        _undo.RemoveAll(Touches);
        _redo.RemoveAll(Touches);
    }

    /// <summary>
    /// Raised when history is cleared or its limit is set to zero during an edit.
    /// Listeners release saved state, such as entity slots kept for redo.
    /// </summary>
    public event Action? Cleared;

    public void Clear()
    {
        _batch = null;
        _undo.Clear();
        _redo.Clear();
        RaiseCleared();
    }

    private void RaiseCleared()
    {
        _lifecycleTargets.Clear();
        if (Cleared is { } observers)
            foreach (Action observer in observers.GetInvocationList())
                try
                {
                    observer();
                }
                catch
                {
                    // Continue notifying the remaining listeners.
                }
    }
}
