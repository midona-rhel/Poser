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
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;
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
    /// <summary>Ordered import targets and their pre-edit states —
    /// captured before anything was written, so a failure restores
    /// exactly what was there and success has a Before half that needs
    /// no re-reading.</summary>
    public required List<TransformTargetId> Order;
    public required Dictionary<TransformTargetId, TransformTargetState> Before;
    /// <summary>Targets the synchronous reset cleared. A reset bone that
    /// had no authored layers did not change and stays out of the
    /// history entry unless a write landed on it.</summary>
    public required HashSet<TransformTargetId> Resets;
    /// <summary>Targets an action (or the model edit) actually wrote.</summary>
    public readonly HashSet<TransformTargetId> Written = new();
    /// <summary>Fires exactly once when an import <see cref="PoseImportCapture.Begin(PoseImportOperation, PoseImportPlan, bool, bool, string?)"/>
    /// returned Ok for finishes — with true after the history entry
    /// landed, false after a rollback. A Begin that returned Fail never
    /// fires it; a pending import dropped by Dispose does not either
    /// (session teardown restores animation state wholesale).</summary>
    public Action<bool>? OnFinished;
    public Action<OperationReceipt>? OnReceipt;
    /// <summary>The import targets the pose library's hidden preview
    /// body. Its changes are scenery, never user edits — they must not
    /// spend the user's undo stack.</summary>
    public required bool PreviewTarget;
    /// <summary>The caller asked for no history entry
    /// (<see cref="PoseImportOptions.SuppressHistory"/>). Its one caller
    /// is an undo restoring a despawned actor's pose: that import is
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

    /// <summary>The pre-import head absolute the restore stage writes
    /// back (position-only; rotation reverts through the stack pop).
    ///
    /// THE SPACE IS THE FIX (repeated-apply head drift, user
    /// 2026-08-10). Begin seeds the cached <c>LastRawTransform</c>,
    /// which is the last settled frame BEFORE the rewind: the facade's
    /// bracket pauses the actor and the settle tick rewinds every
    /// paused control to LocalTime 0 on the very tick it calls Begin
    /// (PoseImportCoordinator.Begin), so no pass has evaluated the
    /// rewound animation yet. Every other write in this chain diffs
    /// against the REWOUND in-pass basis, and the final flatten bakes
    /// its stacks against that same rewound basis — so restoring the
    /// head to a pre-rewind absolute baked
    /// (anim(pause frame) − anim(LocalTime 0)) into the head position
    /// ON TOP of the previous apply's settled state, once per apply:
    /// progressive head drift whenever the animation ran between
    /// applies. <c>ApplyBone</c> therefore overwrites the seed
    /// with the bone's own apply-pass basis — anim(rewound) ⊕ the
    /// pre-import stacks the expression reset deliberately leaves on
    /// the head — which IS the pre-import head expressed in the
    /// chain's one basis. Apply N+1 then restores exactly apply N's
    /// settled head and the restore delta rejects as near-identity.
    /// Brio's pre-rewind capture (tempPose) matches its own brief
    /// bracket — it hands speed back +2 ticks after the import call
    /// (ActionTimelineCapability.cs:169-175), before its reconcile
    /// ever reads the pose; Poser holds the pause through reconcile
    /// and flatten, so the in-pass basis is the only consistent
    /// space.</summary>
    public required Transform PreImport;
}
