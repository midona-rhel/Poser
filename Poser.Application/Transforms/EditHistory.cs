using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>
/// The bounded undo/redo history of every kind of edit: transform patches,
/// journal steps and scene-lifecycle actions (the entries are in
/// HistoryEntry.cs). Capacity is read for each append, and zero clears both
/// stacks while still notifying observers.
/// </summary>
public sealed class EditHistory
{
    /// <summary>The depth used when no setting is supplied (tests, and the
    /// parameterless construction the DI default would use).</summary>
    public const int DefaultCapacity = 500;

    private static readonly Func<int> FixedDefault = static () => DefaultCapacity;

    private readonly Func<int> _capacity;
    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();
    private readonly Dictionary<SelectionId, Func<SelectionId?>> _lifecycleTargets = new();
    private LifecycleHistoryBatch? _batch;

    /// <summary>Records one synchronous multi-entity command. Earlier value edits
    /// are sealed first; only synchronous lifecycle and journal entries belong
    /// to the batch. The callback must not schedule work or cross an await.</summary>
    public void RecordLifecycleBatch(string description, Action removals)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (_batch is not null)
            throw new InvalidOperationException("A lifecycle batch is already recording.");
        BeforeAppend?.Invoke();
        var batch = new LifecycleHistoryBatch(description);
        _batch = batch;
        try { removals(); }
        finally
        {
            if (ReferenceEquals(_batch, batch)) FlushBatch();
        }
    }

    private void FlushBatch()
    {
        var batch = _batch;
        _batch = null;
        if (batch?.Build() is { } entry) Append(entry);
    }

    /// <summary>Keep edits while their entity is deliberately absent. This is
    /// history-only rebinding, never permission to reuse a stale public ID.</summary>
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

    public event Action? PatchAppended;
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

    /// <summary>The entry just appended, for observers that read it — the
    /// action recorder. Observers cannot roll the append back.</summary>
    public event Action<HistoryEntry>? Appended;

    public void Append(HistoryEntry patch)
    {
        BeforeAppend?.Invoke();
        if (_batch is { } batch)
        {
            if (batch.TryAdd(patch)) return;
            // An unexpected contextual/transform edit keeps its own replay
            // semantics and ends collection, rather than hiding its context.
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
        // Observers run after the history mutation and cannot roll it back.
        if (PatchAppended is { } observers)
            foreach (Action observer in observers.GetInvocationList())
                try
                {
                    observer();
                }
                catch
                {
                    // Observers have no transaction authority or result channel.
                }
        if (Appended is { } readers)
            foreach (Action<HistoryEntry> reader in readers.GetInvocationList())
                try
                {
                    reader(patch);
                }
                catch
                {
                    // Same rule: a reader cannot fail the append.
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
            // Lifecycle replay can remove the selected entity, leaving no live
            // target for the opposite shortcut. Keep it global in both directions,
            // but use its known footprint to skip it for unrelated entity edits.
            // Shared steps likewise cannot be split or skipped for one member.
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
        // Attached targets depend on another entity's frame. Keep their edits
        // ordered globally rather than treating the child as independent.
        if (states.Any(state => state.Parent is not null)) return null;
        return states.Select(state => EntityOf(state.Target.ToSelectionId())).Distinct().ToArray();
    }

    /// <summary>
    /// Drops every patch touching a target that is no longer current. A
    /// patch is removed whole when ANY of its targets is stale (its restore
    /// could never succeed); patches whose targets all remain current
    /// survive, so replacing one slot never discards history that involves
    /// only unaffected slots or actors.
    ///
    /// <para>A lifecycle entry is never stale by this rule and is never
    /// dropped: it holds no target state, and the entity it names is
    /// deliberately absent for exactly half of its life — an "add light"
    /// entry whose light has been undone away is precisely the entry that
    /// must survive to be redone.</para>
    /// </summary>
    public void Reconcile(
        Func<Poser.Domain.Identity.TransformTargetId, bool> isCurrent,
        Func<Poser.Domain.Identity.TransformTargetId, Poser.Domain.Identity.TransformTargetId?>? rekey = null)
    {
        RefreshLifecycleTargets(_undo);
        RefreshLifecycleTargets(_redo);
        // A patch whose targets went stale is first RE-KEYED: a bone edit
        // survives the actor's redraw by naming the same bone on the new
        // body, as Brio's whole-pose snapshot does (ruled 2026-09-03). Only
        // a patch that cannot be re-keyed is dropped.
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
                    continue;
                var before = Remap(patch.Before);
                var after = before is null ? null : Remap(patch.After);
                var group = patch.GroupState?.Remap(target =>
                    isCurrent(target) ? target : rekey(target));
                if (before is not null && after is not null
                    && (patch.GroupState == null || group != null))
                    stack[i] = patch with { Before = before, After = after, GroupState = group };
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
            bool staleTarget =
                patch.Before.Any(state => !isCurrent(state.Target) && !_lifecycleTargets.ContainsKey(state.Target.ToSelectionId())) ||
                patch.After.Any(state => !isCurrent(state.Target) && !_lifecycleTargets.ContainsKey(state.Target.ToSelectionId()));
            if (!staleTarget)
                return false;
            return true;
        }
        Rekey(_undo);
        Rekey(_redo);
        _undo.RemoveAll(entry => Stale(entry));
        _redo.RemoveAll(entry => Stale(entry));
    }

    /// <summary>Forgets one entry wherever it sits, without an event: the
    /// journal's answer to a restore that outlived its place.</summary>
    public void Drop(HistoryEntry entry)
    {
        _undo.Remove(entry);
        _redo.Remove(entry);
    }

    /// <summary>Drops transform edits for an entity that a lifecycle release
    /// leaves absent and cannot safely restore. A grouped edit is removed as
    /// one action when any member names that entity.</summary>
    public void DropTransformsFor(TransformTargetId target)
    {
        bool Touches(HistoryEntry entry) => entry is TransformPatch patch
            && (patch.Before.Any(state => state.Target == target)
                || patch.After.Any(state => state.Target == target));
        _undo.RemoveAll(Touches);
        _redo.RemoveAll(Touches);
    }

    /// <summary>
    /// Raised whenever both stacks are emptied — by <see cref="Clear"/>, and
    /// equally by the undo-off branch of <see cref="Append"/>, which empties
    /// them just as thoroughly.
    ///
    /// <para>A lifecycle entry closes over state its owner keeps beside the
    /// stack — the slot that re-binds an entity across a destroy/respawn pair
    /// — and that state is meaningless once the entries naming it are gone.
    /// Announcing the emptying keeps the answer in ONE place: whoever put
    /// state behind an entry drops it here, rather than every site that
    /// empties the stacks having to remember a second sweep. This class's own
    /// Append forgot exactly that, which is the point.</para>
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
                    // Observers have no transaction authority or result
                    // channel, exactly as for PatchAppended.
                }
    }
}
