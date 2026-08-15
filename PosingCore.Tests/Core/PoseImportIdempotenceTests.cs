using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Core;

/// <summary>
/// Re-applying the SAME pose file must converge after the first apply
/// (user 2026-08-10: the head drifted a little more per apply while Brio
/// converged). The full capture chain lives in Poser.Game and needs the
/// native pass, so these tests pin the PURE halves the fix rests on:
///
/// <para>1. A plan built from a state that already equals the file
/// produces only near-identity deltas — a repeat apply appends nothing
/// (the engine's early-out is taken on exactly this delta).</para>
///
/// <para>2. The expression head dance (phase-1 write → pop →
/// position-only restore → whole-pose flatten) is a fixed point when the
/// restore target is expressed in the SAME basis the pass diffs against —
/// and compounds by exactly the pre-rewind animation offset per apply
/// when it is not, which was the drift
/// (PoseImportCapture.HeadRestore.PreImport).</para>
///
/// <para>3. The flatten's export-reset-reimport cycle is a fixed point
/// with a static named service layer in the stack, because export and
/// basis both contain the layer's contribution.</para>
/// </summary>
public class PoseImportIdempotenceTests
{
    /// <summary>The rewound animation frame every in-pass basis of the
    /// import chain is built on (the facade pauses, then rewinds every
    /// paused control to LocalTime 0 before arming).</summary>
    private static readonly Transform Anim0 = new(
        new Vector3(0f, 1.5f, 0.02f),
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.05f),
        Vector3.One);

    /// <summary>The file's head — an expression that turns the head.</summary>
    private static readonly Transform FileHead = new(
        new Vector3(0.03f, 1.55f, 0f),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
        Vector3.One);

    /// <summary>Where the running idle happens to be when the user clicks
    /// apply — the offset between the pause-caught frame and LocalTime 0.
    /// Constant across applies models the worst case: users re-apply at a
    /// similar animation phase, so the offsets share a direction.</summary>
    private static readonly Vector3 IdleSway = new(0.002f, 0.001f, 0f);

    // ---- engine semantics mirrored 1:1 -------------------------------

    /// <summary>PoseImportCapture.IsApproximatelyIdentity — the engine's
    /// caller-side near-identity early-out on a masked delta.</summary>
    private static bool IsApproximatelyIdentity(Transform delta)
    {
        const float tolerance = 0.000001f;
        return MathF.Abs(delta.Position.X) < tolerance &&
               MathF.Abs(delta.Position.Y) < tolerance &&
               MathF.Abs(delta.Position.Z) < tolerance &&
               MathF.Abs(delta.Scale.X) < tolerance &&
               MathF.Abs(delta.Scale.Y) < tolerance &&
               MathF.Abs(delta.Scale.Z) < tolerance &&
               MathF.Abs(MathF.Abs(delta.Rotation.W) - 1f) < tolerance;
    }

    /// <summary>One stack delta applied the way the apply pass writes it:
    /// position and scale add in model space, rotation post-multiplies
    /// (BonePosingService.ApplyBoneTransform, BoneLocal frame).</summary>
    private static Transform ApplyDelta(Transform basis, Transform delta) => new()
    {
        Position = basis.Position + delta.Position,
        Rotation = Quaternion.Normalize(basis.Rotation * delta.Rotation),
        Scale = basis.Scale + delta.Scale,
    };

    /// <summary>The bone's settled model-space state for a frame: the
    /// animated basis with every stack applied in list order — what the
    /// pass leaves in LastRawTransform and what an export reads.</summary>
    private static Transform Settle(Transform anim, BonePoseInfo info)
    {
        var result = anim;
        foreach (var stack in info.Stacks)
            result = ApplyDelta(result, stack.Transform);
        return result;
    }

    private static void AssertWithin(
        Transform expected, Transform actual, float tolerance, string what)
    {
        Assert.True(
            Vector3.Distance(expected.Position, actual.Position) <= tolerance,
            $"{what}: position drifted {Vector3.Distance(expected.Position, actual.Position)}");
        Assert.True(
            MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) >= 1f - tolerance,
            $"{what}: rotation drifted");
        Assert.True(
            Vector3.Distance(expected.Scale, actual.Scale) <= tolerance,
            $"{what}: scale drifted");
    }

    // ---- the expression head dance, one bone, N applies --------------

    /// <summary>
    /// The engine's per-apply head lifecycle on j_kao, exactly as
    /// PoseImportCapture stages it: the expression reset skips the head;
    /// phase 1 forces the file's head onto its own stack; the restore
    /// stage pops that stack and writes a position-only restore; the
    /// flatten exports the settled pose, clears every interactive stack
    /// and re-imports the export against the rewound basis. Between
    /// applies the idle animation runs, so the frame the NEXT pause
    /// catches sits <see cref="IdleSway"/> away from LocalTime 0.
    /// <paramref name="restoreFromPreRewindFrame"/> selects the restore
    /// target's space: false is the fix (the apply pass's own basis),
    /// true is the Begin-time cached LastRawTransform that predates the
    /// rewind — the drift.
    /// </summary>
    private static List<Transform> SimulateExpressionApplies(
        int applies, bool restoreFromPreRewindFrame)
    {
        var head = new BonePoseInfo("j_kao", 0);
        var settledStates = new List<Transform>(applies);

        for (var apply = 0; apply < applies; apply++)
        {
            // Begin-time cached LastRawTransform: the last settled frame,
            // which the rewind has NOT reached yet — pre-rewind animation
            // with the surviving stacks on top.
            var animAtPause = new Transform(
                Anim0.Position + IdleSway, Anim0.Rotation, Anim0.Scale);
            var preRewindHead = Settle(animAtPause, head);

            // Apply pass: every basis from here on is the REWOUND frame.
            // The expression reset deliberately leaves j_kao's stacks.
            var basisWithStacks = Settle(Anim0, head);
            Assert.NotNull(head.Apply(FileHead, basisWithStacks,
                TransformComponents.All, TransformComponents.All,
                forceNewStack: true));

            // Head restore: pop exactly the phase-1 stack…
            Assert.True(head.RemoveLastInteractiveStack());
            var postPop = Settle(Anim0, head);

            // …then the position-only restore, diffed in-pass against the
            // post-pop basis, with the engine's near-identity early-out.
            var restoreTarget = restoreFromPreRewindFrame
                ? preRewindHead
                : basisWithStacks;
            var restoreDelta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(restoreTarget, postPop),
                TransformComponents.Position);
            if (!IsApproximatelyIdentity(restoreDelta))
                Assert.NotNull(head.Apply(restoreTarget, postPop,
                    TransformComponents.All, TransformComponents.Position,
                    forceNewStack: true));

            // Flatten: export the settled pose, clear every interactive
            // stack, re-import the export whole against the rewound basis.
            var exported = Settle(Anim0, head);
            Assert.True(head.RestoreInteractiveStacks(
                Array.Empty<BonePoseTransformInfo>()));
            Assert.NotNull(head.Apply(exported, Anim0,
                TransformComponents.All, TransformComponents.All,
                forceNewStack: true));

            settledStates.Add(Settle(Anim0, head));
        }

        return settledStates;
    }

    [Fact]
    public void HeadRestore_InImportBasis_ConvergesAcross100Applies()
    {
        var settled = SimulateExpressionApplies(
            100, restoreFromPreRewindFrame: false);

        // Apply 1 is the transition onto the file; every later apply must
        // land bit-tight on the same state — the acceptance bar.
        for (var i = 1; i < settled.Count; i++)
            AssertWithin(settled[0], settled[i], 1e-4f, $"apply {i + 1}");
    }

    [Fact]
    public void HeadRestore_InImportBasis_RestoreDeltaIsIdentity()
    {
        // With the target expressed in the pass's own basis the restore
        // ALWAYS rejects as near-identity: the pop already put the head
        // back, so the head ends every apply with exactly one stack (the
        // flatten's) — no restore residue accumulates.
        var head = new BonePoseInfo("j_kao", 0);
        for (var apply = 0; apply < 5; apply++)
        {
            var basisWithStacks = Settle(Anim0, head);
            head.Apply(FileHead, basisWithStacks,
                TransformComponents.All, TransformComponents.All,
                forceNewStack: true);
            head.RemoveLastInteractiveStack();
            var postPop = Settle(Anim0, head);
            var restoreDelta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(basisWithStacks, postPop),
                TransformComponents.Position);
            Assert.True(IsApproximatelyIdentity(restoreDelta));

            var exported = Settle(Anim0, head);
            head.RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
            head.Apply(exported, Anim0,
                TransformComponents.All, TransformComponents.All,
                forceNewStack: true);
            Assert.Single(head.Stacks);
        }
    }

    [Fact]
    public void HeadRestore_FromPreRewindFrame_CompoundsPerApply()
    {
        // The defect this file regression-tests: a restore target captured
        // BEFORE the LocalTime rewind bakes (pause frame − LocalTime 0)
        // into the head once per apply, on top of the previous settled
        // state — linear drift, the reported symptom. If this test starts
        // failing because the drift is GONE, the simulation no longer
        // matches the engine's staging; keep them in lockstep.
        var settled = SimulateExpressionApplies(
            100, restoreFromPreRewindFrame: true);

        var perApply = Vector3.Distance(
            settled[1].Position, settled[2].Position);
        Assert.True(
            MathF.Abs(perApply - IdleSway.Length()) < 1e-4f,
            $"per-apply drift {perApply} should equal the idle offset {IdleSway.Length()}");

        var total = Vector3.Distance(
            settled[0].Position, settled[^1].Position);
        Assert.True(
            total > 0.1f,
            $"100 applies should have compounded well past visibility, got {total}");
    }

    // ---- the flatten cycle with a static named layer -----------------

    [Fact]
    public void Flatten_WithStaticNamedLayer_IsFixedPoint()
    {
        // The flatten's export contains the live service layer's
        // contribution AND its in-pass basis contains the same value
        // (named layers survive the reset and are applied before the
        // transitive action reads the basis), so a static layer cancels
        // out of the baked delta exactly — nothing of the layer is ever
        // re-baked as authored state, no matter how many times the
        // flatten runs.
        var bone = new BonePoseInfo("j_f_eye_l", 1);
        Assert.True(bone.SetLayerTransform(
            "expression",
            new Transform(
                new Vector3(0.001f, 0.002f, 0f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f),
                Vector3.Zero),
            TransformComponents.None));

        // An authored edit on top of the layer.
        Assert.NotNull(bone.Apply(
            new Transform(
                new Vector3(0.01f, 1.4f, 0.03f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f),
                Vector3.One),
            Settle(Anim0, bone)));

        var first = Settle(Anim0, bone);
        for (var i = 0; i < 100; i++)
        {
            var exported = Settle(Anim0, bone);
            Assert.True(bone.RestoreInteractiveStacks(
                Array.Empty<BonePoseTransformInfo>()));
            // Post-reset basis: the animation with the surviving layer.
            Assert.NotNull(bone.Apply(exported, Settle(Anim0, bone),
                TransformComponents.All, TransformComponents.All,
                forceNewStack: true));

            AssertWithin(first, Settle(Anim0, bone), 1e-4f, $"flatten {i + 1}");
        }

        Assert.Contains(bone.Stacks, stack => stack.Layer == "expression");
    }

    // ---- plan-level: a converged state re-imports as identity --------

    private static IBone MakeBone(
        ISkeleton skeleton,
        string name,
        int partialId,
        int boneIndex,
        bool isPartialRoot,
        Transform lastRaw)
    {
        var bone = Substitute.For<IBone>();
        bone.BoneName.Returns(name);
        bone.PartialId.Returns(partialId);
        bone.BoneIndex.Returns(boneIndex);
        bone.IsPartialRoot.Returns(isPartialRoot);
        bone.IsSkeletonRoot.Returns(false);
        bone.LastRawTransform.Returns(lastRaw);
        bone.Skeleton.Returns(skeleton);
        bone.ParentBone.Returns((IBone?)null);
        return bone;
    }

    [Fact]
    public void PlanFromStateAlreadyAtFile_ProducesOnlyIdentityWrites()
    {
        // The layering route (reset off): exporting the settled pose and
        // re-importing it must diff every write to near-identity against
        // each instance's own LastRawTransform — including the face
        // partial ROOT, which the export skips but the import's
        // per-instance fan-out writes from the body head's entry. At the
        // converged state the reparent has snapped the root onto the body
        // head, so both instances carry the same absolute and both
        // deltas reject; a repeat apply appends nothing anywhere.
        var actor = Substitute.For<IActor>();
        var skeleton = Substitute.For<ISkeleton>();
        skeleton.Slot.Returns(PoseSlot.Character);
        skeleton.Actor.Returns(actor);
        skeleton.GetBone(Arg.Any<string>()).Returns((IBone?)null);

        var headPose = new Transform(
            new Vector3(0.02f, 1.6f, 0.01f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f),
            Vector3.One);
        var browPose = new Transform(
            new Vector3(0.05f, 1.68f, 0.09f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.1f),
            Vector3.One);
        var spinePose = new Transform(
            new Vector3(0f, 1.1f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.15f),
            Vector3.One);

        var bones = new List<IBone>
        {
            MakeBone(skeleton, "j_sebo_a", 0, 2, false, spinePose),
            MakeBone(skeleton, "j_kao", 0, 5, false, headPose),
            // The face partial's root instance: post-reparent it holds the
            // body head's absolute. Skipped by the export, written by the
            // import fan-out.
            MakeBone(skeleton, "j_kao", 1, 0, true, headPose),
            MakeBone(skeleton, "j_f_mayu_l", 1, 3, false, browPose),
        };
        skeleton.Bones.Returns(bones);

        var posing = Substitute.For<IPosingService>();
        posing.GetEffectiveTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        posing.GetOriginalTransform(Arg.Any<IActor>()).Returns(Transform.Identity);
        var service = new PoseFileService(Substitute.For<IPluginLog>(), posing);

        var slots = new List<ISkeleton> { skeleton };
        var poseFile = service.CreatePoseFile(slots);

        // The export must have taken the BODY head, not the skipped root.
        Assert.Equal(3, poseFile.Bones.Count);
        Assert.Equal(headPose.Position, poseFile.Bones["j_kao"].Position);

        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = true,
            ApplyBody = true,
            ApplyFace = true,
            ApplyModelTransform = false,
            ResetBeforeImport = false,
        };
        var plan = service.BuildImportPlan(slots, poseFile, options);

        // Fan-out: the one j_kao file entry reaches BOTH live instances.
        Assert.Equal(2, plan.Writes.Count(write => write.Bone.BoneName == "j_kao"));
        Assert.Equal(4, plan.Writes.Count);

        foreach (var (bone, file, components) in plan.Writes)
        {
            var delta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(file, bone.LastRawTransform), components);
            Assert.True(
                IsApproximatelyIdentity(delta),
                $"{bone.BoneName} (p{bone.PartialId}) would re-write a converged state");
        }
    }
}
