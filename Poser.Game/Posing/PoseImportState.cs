using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Domain.Operations;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;
using Poser.Documents.Files;

namespace Poser.Game.Posing;

/// <summary>One slot's share of an import: the plan's writes keyed by
/// (partial, bone name) — the pass hands the LIVE bone to the callback,
/// so no skeleton or bone instance is held across ticks (issue #78). A
/// redraw between registration and the pass simply means the callback
/// matches the replacement skeleton's bones by name.</summary>
internal sealed class PoseImportSlot
{
    public required PoseSlot Slot;
    public required Dictionary<(int Partial, string Bone),
        (TransformTargetId Target, Transform File, TransformComponents Components)> Writes;
    public bool Ended;
    public bool Executed;
}

/// <summary>Which transitive batch the pending import is waiting on:
/// the plan's file writes, the expression import's head restore, the
/// post-reparent face reconcile, or the expression import's final
/// whole-pose flatten.</summary>
internal enum PoseImportStage
{
    Apply,
    HeadRestore,
    Reconcile,
    Flatten,
}

internal sealed class PendingPoseImport
{
    public required long Generation;
    public required Guid OperationId;
    public required OperationEpoch OperationEpoch;
    public required SessionGeneration SessionGeneration;
    public required ActorId TargetActorId;
    /// <summary>The target actor's stable legacy key
    /// (<c>actor.Id.Unique</c>): the pose store's actor address and the
    /// key transitive-batch outcomes are matched by. A string, never a
    /// wrapper instance — the import holds no instance across ticks.
    /// </summary>
    public required string ActorKey;
    public required PoseImportOperation Operation;
    public required IReadOnlyList<TransformTargetId> Targets;
    public required string Description;
    /// <summary>The CURRENT stage's batches. <c>BeginReconcile</c>
    /// replaces the verified apply slots with the one reconcile slot.</summary>
    public required List<PoseImportSlot> Slots;
    /// <summary>The plan's file writes, kept when later phases replace
    /// <see cref="Slots"/> with their own batches.</summary>
    public readonly List<PoseImportSlot> ApplySlots = new();
    /// <summary>Driven-bone correction rounds run so far.</summary>
    public int DrivenRounds;
    /// <summary>Ordered import targets and their pre-edit states —
    /// captured before each target is first changed. Rollback uses these
    /// states and reports any restore failures.</summary>
    public required List<TransformTargetId> Order;
    public required Dictionary<TransformTargetId, TransformTargetState> Before;
    /// <summary>Targets the synchronous reset cleared. A reset bone that
    /// had no authored layers did not change and stays out of the
    /// history entry unless a write landed on it.</summary>
    public required HashSet<TransformTargetId> Resets;
    /// <summary>Targets an action (or the model edit) actually wrote.</summary>
    public readonly HashSet<TransformTargetId> Written = new();
    /// <summary>Completion callback: true for an applied receipt, false for
    /// other terminal states, including failed or incomplete rollback.</summary>
    public Action<bool>? OnFinished;
    public Action<OperationReceipt>? OnReceipt;
    /// <summary>The import targets the pose library's hidden preview
    /// body. Its changes are scenery, never user edits — they must not
    /// spend the user's undo stack.</summary>
    public required bool PreviewTarget;
    /// <summary>The caller asked for no history entry
    /// (<see cref="PoseImportOptions.SuppressHistory"/>). For example, undo
    /// may restore a despawned actor's pose: that import is
    /// already inside the history's own walk, and an append there would
    /// clear the redo stack the walk had just pushed onto.</summary>
    public bool SuppressHistory;
    /// <summary>The file the import came from, when it came from one.</summary>
    public string? Asset;
    public PoseImportStage Stage = PoseImportStage.Apply;
    /// <summary>Whether this is an expression import — it runs the head
    /// restore and, at the very end, Brio's whole-pose flatten
    /// (Reconcile(reset: true), PosingCapability.cs:417-429): the phase-2
    /// call leaves ImportPose_Internal's reset/reconcile at their TRUE
    /// defaults, so unlike a body import (reconcile: false, :156) the
    /// expression chain finishes by exporting the entire visual pose,
    /// clearing every stack, and re-importing it whole.</summary>
    public bool Expression;
    /// <summary>Expression imports only: every j_kao instance the plan
    /// writes, with the pre-import absolute the head-restore stage puts
    /// back — Brio's tempPose reduced to the one bone its
    /// expressionPhase2 actually uses (PosingCapability.cs:194,
    /// PoseImporter.cs:11-26). The head lands transiently in the apply
    /// stage so the face computes its deltas in the FILE's head space;
    /// this restores it. Seeded at Begin, RE-EXPRESSED by the apply
    /// pass in its own basis — see <see cref="PoseImportHeadRestore.PreImport"/>
    /// for why the space is the whole point.</summary>
    public List<PoseImportHeadRestore>? HeadRestores;
    /// <summary>Whether the plan wrote any Character-slot bone of a
    /// non-zero partial — the only writes whose export/basis spaces can
    /// disagree, so the only imports a reconcile can converge. The
    /// Character skeleton itself is resolved fresh on each stage's own
    /// tick; the import never carries the instance.</summary>
    public bool WroteFacePartial;
    /// <summary>Whether the plan wrote any Character-slot bone at all —
    /// the head-restore and flatten stages exist only for such
    /// imports.</summary>
    public bool WroteCharacter;
    public string? Failure;
    public bool Completing;
    public bool Invalidated;
    public OperationReceipt? PendingReceipt;
    public bool TerminalPublished;
    public bool MutationStarted;
    public readonly PoseImportInvalidation Invalidation = new();
}

internal sealed class PoseImportInvalidation
{
    private int _invalidated;
    public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
    public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);
}

/// <summary>One j_kao instance's target for the expression head restore,
/// addressed by (partial, name) like every other bone reference above
/// the write layer — the apply pass matches the LIVE head bone by name.
/// </summary>
internal sealed class PoseImportHeadRestore
{
    public required int Partial;
    public required string Bone;
    public required TransformTargetId Target;

    /// <summary>Head position to restore after the expression write. Begin seeds
    /// this from the cache; ApplyBone replaces it with the current pass's basis.
    /// Using the older, pre-rewind cache would add the animation's movement to
    /// the head on repeated imports. Rotation is restored by removing the stack.</summary>
    public required Transform PreImport;
}
