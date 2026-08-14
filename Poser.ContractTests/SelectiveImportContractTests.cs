using NSubstitute;
using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.ContractTests;

/// <summary>
/// Contract tests for the 11G selective-import slice: selected-bones and
/// descendant-subtree file import and the native reference-pose action, all
/// entering through the ONE accepted pose-import transaction
/// (CleanPoseFacade.ImportPose → PoseImportCapture) — no second transaction
/// or receipt type. The invariants: a frozen selection reduces to the exact
/// slot-qualified filter only when every bone belongs to the exact target
/// generation and still resolves; empty/stale/cross-actor selections are
/// typed refusals BEFORE any arm, receipt, or history mutation; descendant
/// expansion is the plan builder's same-slot ancestor walk; the reference
/// pose applies rotation+position but never scale (Ktisis
/// PosingManager.ApplyReferencePose memento parity) and publishes through
/// the same receipt surface.
/// </summary>
public sealed class SelectiveImportContractTests
{
    private static PoseImportPlan PlanWithOneWrite(PoseImportCaptureHarness app)
    {
        var plan = new PoseImportPlan { FileBoneCount = 1 };
        plan.Writes.Add((app.Bone, Transform.Identity, TransformComponents.All));
        return plan;
    }

    private static BoneId SnapshotBone(PoseImportCaptureHarness app) =>
        app.Scene.Snapshot.Actors[0].Skeletons[0].Bones[0].Id;

    /// <summary>Expression-tree-safe predicate: the reduced options carry
    /// exactly the head bone as a slot-qualified filter with descendants on
    /// (NSubstitute's Arg.Is cannot hold a tuple literal inline).</summary>
    private static bool IsReducedHeadFilter(PoseImportOptions built) =>
        built.BoneFilter != null
        && built.BoneFilter.Count == 1
        && built.BoneFilter.Contains((PoseSlot.Character, "j_kao"))
        && built.FilterIncludesDescendants;

    // ── Selected-bones reduction and refusals ────────────────────────────

    [Fact]
    public void Empty_selection_is_a_typed_refusal_before_any_arm_or_receipt()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();

        var result = app.Facade.ImportPose(
            app.Actor, new PoseFile(), new PoseImportOptions(),
            "selected import", receipts.Add, Array.Empty<BoneId>());

        Assert.False(result.Success);
        Assert.Contains("selected", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(receipts);
        Assert.False(app.Imports.IsPending);
        Assert.False(app.History.CanUndo);
        app.PoseFiles.DidNotReceiveWithAnyArgs().BuildImportPlan(
            default!, default(PoseFile)!, default);
    }

    [Fact]
    public void Stale_selected_bone_is_a_typed_refusal_before_any_arm()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        var stale = SnapshotBone(app);
        stale = stale with { Skeleton = stale.Skeleton.NextGeneration() };

        var result = app.Facade.ImportPose(
            app.Actor, new PoseFile(), new PoseImportOptions(),
            "selected import", receipts.Add, new[] { stale });

        Assert.False(result.Success);
        Assert.Empty(receipts);
        Assert.False(app.Imports.IsPending);
        app.PoseFiles.DidNotReceiveWithAnyArgs().BuildImportPlan(
            default!, default(PoseFile)!, default);
    }

    [Fact]
    public void Cross_actor_selected_bone_is_a_typed_refusal_before_any_arm()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        var bone = SnapshotBone(app);
        var foreign = bone with
        {
            Skeleton = bone.Skeleton with { Actor = ActorId.New() },
        };

        var result = app.Facade.ImportPose(
            app.Actor, new PoseFile(), new PoseImportOptions(),
            "selected import", receipts.Add, new[] { foreign });

        Assert.False(result.Success);
        Assert.Contains("different actor", result.Detail!);
        Assert.Empty(receipts);
        Assert.False(app.Imports.IsPending);
    }

    [Fact]
    public void Valid_selection_reduces_to_the_exact_slot_qualified_filter_and_arms_one_operation()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.SetNextPlan(PlanWithOneWrite(app));
        var bone = SnapshotBone(app);
        var options = new PoseImportOptions { FilterIncludesDescendants = true };

        var result = app.Facade.ImportPose(
            app.Actor, new PoseFile(), options,
            "selected import", receipts.Add, new[] { bone });

        Assert.True(result.Success, result.Detail);
        Assert.Equal(OperationReceiptState.Pending, Assert.Single(receipts).State);
        Assert.Equal(app.ActorId, receipts[0].TargetActorId);
        Assert.True(app.Imports.IsPending);
        app.PoseFiles.Received(1).BuildImportPlan(
            Arg.Any<IReadOnlyList<ISkeleton>>(),
            Arg.Any<PoseFile>(),
            Arg.Is<PoseImportOptions>(built => IsReducedHeadFilter(built)));
        // The caller's own options object stays untouched — the reduction
        // works on a clone.
        Assert.Null(options.BoneFilter);
    }

    [Fact]
    public void Selective_arm_refuses_a_second_import_while_pending()
    {
        using var app = new PoseImportCaptureHarness();
        app.SetNextPlan(PlanWithOneWrite(app));
        var bone = SnapshotBone(app);
        var first = new List<OperationReceipt>();
        var second = new List<OperationReceipt>();

        Assert.True(app.Facade.ImportPose(
            app.Actor, new PoseFile(), new PoseImportOptions(),
            "first", first.Add, new[] { bone }).Success);
        var result = app.Facade.ImportPose(
            app.Actor, new PoseFile(), new PoseImportOptions(),
            "second", second.Add, new[] { bone });

        // The accepted transaction supersedes rather than stacking: the
        // second arm succeeds, the FIRST operation's receipt terminates
        // Cancelled, and exactly one pending operation survives.
        Assert.True(result.Success, result.Detail);
        Assert.Equal(OperationReceiptState.Cancelled, first[^1].State);
        Assert.Equal(OperationReceiptState.Pending, Assert.Single(second).State);
        Assert.True(app.Imports.IsPending);
    }

    // ── Descendant subtree expansion (real plan builder) ─────────────────

    private static PoseFileService RealFileService() => new(
        Substitute.For<Dalamud.Plugin.Services.IPluginLog>(),
        Substitute.For<IPosingService>());

    private static PoseFile FileWith(params string[] bones)
    {
        var file = new PoseFile();
        foreach (var name in bones)
            file.Bones[name] = new PoseFile.BoneData
            {
                Rotation = System.Numerics.Quaternion.Identity,
                Scale = System.Numerics.Vector3.One,
            };
        return file;
    }

    [Fact]
    public void Filter_without_descendants_plans_only_the_filtered_bone()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyBody = true,
            ApplyFace = true,
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = false,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao", "j_mab_l"), options);

        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_kao");
        Assert.DoesNotContain(plan.Writes, write => write.Bone.BoneName == "j_mab_l");
    }

    [Fact]
    public void Filter_with_descendants_plans_the_whole_subtree()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyBody = true,
            ApplyFace = true,
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = true,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao", "j_mab_l"), options);

        // j_mab_l's parent chain reaches j_kao on the same slot, so the
        // subtree rides the filter (PoseFileService.ClassifyBoneFilter's
        // ancestor walk).
        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_kao");
        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_mab_l");
    }

    // ── Direct selection bypasses the mode gates (Ktisis parity) ─────────

    [Fact]
    public void Directly_selected_bone_applies_under_narrowed_mode_gates()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        // Every mode gate narrowed against j_kao at once: body scope off,
        // face gate off, and a category exclusion banning the whole j_
        // prefix. Ktisis applies a directly selected bone regardless
        // (PoseContainer.ApplyToBones has no partial-mode gate); a
        // selection the user made bone by bone must never silently drop.
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyBody = false,
            ApplyFace = false,
            ExcludedBonePrefixes = new HashSet<string> { "j_" },
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = false,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao", "j_mab_l"), options);

        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_kao");
        Assert.DoesNotContain(plan.Writes, write => write.Bone.BoneName == "j_mab_l");
    }

    [Fact]
    public void Descendant_expansion_still_respects_the_mode_gates()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyBody = true,
            ApplyFace = true,
            // The gate bans only the descendant: the direct selection
            // applies, its expanded subtree respects the exclusion —
            // Ktisis' modes gate expansion, never the explicit selection.
            ExcludedBonePrefixes = new HashSet<string> { "j_mab" },
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = true,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao", "j_mab_l"), options);

        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_kao");
        Assert.DoesNotContain(plan.Writes, write => write.Bone.BoneName == "j_mab_l");
    }

    [Fact]
    public void Directly_selected_weapon_bone_applies_with_its_slot_disabled()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var file = new PoseFile();
        file.MainHand["n_hara"] = new PoseFile.BoneData
        {
            Rotation = System.Numerics.Quaternion.Identity,
            Scale = System.Numerics.Vector3.One,
        };
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyMainHand = false,
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.MainHand, "n_hara") },
            FilterIncludesDescendants = false,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton, app.WeaponSkeleton }, file, options);

        // The slot enables are mode gates too: the disabled MainHand slot
        // still admits the DIRECTLY selected bone.
        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "n_hara");
    }

    // ── Anchor positions (Ktisis "Anchor group positions") ───────────────

    [Fact]
    public void Anchored_selection_keeps_position_while_the_other_components_apply()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = false,
            AnchorSelectedPositions = true,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao"), options);

        // Ktisis restores the selection's pre-import positions after the
        // selective apply (PosingManager.cs:254-265); the planned mask is
        // the same net pose — rotation lands, position is withheld.
        var write = Assert.Single(plan.Writes);
        Assert.Equal("j_kao", write.Bone.BoneName);
        Assert.True(write.Components.HasFlag(TransformComponents.Rotation));
        Assert.False(write.Components.HasFlag(TransformComponents.Position));
    }

    [Fact]
    public void Anchor_with_descendants_masks_the_whole_selective_set()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            BoneFilter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") },
            FilterIncludesDescendants = true,
            AnchorSelectedPositions = true,
        };

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao", "j_mab_l"), options);

        // Ktisis' restore set is GetSelectedBones(false, includeDescendants)
        // — descendants ride the anchor (PosingManager.cs:255), so the
        // expanded subtree holds position exactly like the direct selection.
        Assert.Equal(2, plan.Writes.Count);
        Assert.All(plan.Writes, write =>
        {
            Assert.True(write.Components.HasFlag(TransformComponents.Rotation));
            Assert.False(write.Components.HasFlag(TransformComponents.Position));
        });
    }

    [Fact]
    public void Anchor_without_a_position_component_is_inert_and_a_position_only_anchor_empties_the_plan()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();
        var filter = new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") };

        // No position importing: the anchor has nothing to withhold — the
        // Ktisis gate (selective AND Position applying) leaves the plan
        // exactly as without it.
        var inert = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao"),
            new PoseImportOptions
            {
                ApplyRotation = true,
                ApplyPosition = false,
                BoneFilter = new HashSet<(PoseSlot, string)>(filter),
                AnchorSelectedPositions = true,
            });
        var write = Assert.Single(inert.Writes);
        Assert.True(write.Components.HasFlag(TransformComponents.Rotation));

        // Position-only + anchor masks every write to nothing: the plan
        // comes back EMPTY, and the transaction's existing empty-plan gate
        // ("Nothing in this file applies to the chosen scope") turns that
        // into the typed refusal — never a silent zero-bone arm.
        var emptied = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao"),
            new PoseImportOptions
            {
                ApplyRotation = false,
                ApplyPosition = true,
                BoneFilter = new HashSet<(PoseSlot, string)>(filter),
                AnchorSelectedPositions = true,
            });
        Assert.Empty(emptied.Writes);
        Assert.True(emptied.IsEmpty);
    }

    [Fact]
    public void The_default_smart_import_selective_flow_applies_position_so_the_anchor_is_live()
    {
        using var app = new PoseImportCaptureHarness();
        var service = RealFileService();

        // The dialog's DEFAULT state, derived exactly as the descendant-less
        // selective confirm derives it (RouteAsType → ForImportType with the
        // plain Body pair, presetComponents from Smart Import which is on by
        // default): the raw Position checkbox is FALSE and disabled, and the
        // preset still imports positions. The anchor row gates on THIS value
        // (PoseFileInspectorSection.SelectiveImportAppliesPosition), never on
        // the checkbox — Ktisis reads the transform its apply consumes
        // (PoseImportDialog.cs:151-153 → :199).
        var options = PoseImportOptions.ForImportType(
            body: true, expression: false,
            rotation: true, position: false, scale: false,
            presetComponents: true);
        Assert.True(options.ApplyPosition);

        options.BoneFilter =
            new HashSet<(PoseSlot, string)> { (PoseSlot.Character, "j_kao") };
        options.AnchorSelectedPositions = true;

        var plan = service.BuildImportPlan(
            new[] { app.Skeleton }, FileWith("j_kao"), options);

        // ... and the anchor those defaults reach actually anchors.
        var write = Assert.Single(plan.Writes);
        Assert.Equal("j_kao", write.Bone.BoneName);
        Assert.True(write.Components.HasFlag(TransformComponents.Rotation));
        Assert.False(write.Components.HasFlag(TransformComponents.Position));

        // The gate still closes where position genuinely will not apply: with
        // descendants on, the built pair stands, and Smart Import's
        // neither-type preset is rotation-only — the row disables and the
        // anchor would have nothing to withhold.
        Assert.False(PoseImportOptions.ForImportType(
            body: false, expression: false,
            rotation: true, position: false, scale: false,
            presetComponents: true).ApplyPosition);
    }

    // ── Reference pose ───────────────────────────────────────────────────

    [Fact]
    public void Reference_pose_publishes_one_pending_receipt_through_the_same_transaction()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.SetNextPlan(PlanWithOneWrite(app));
        app.Skeleton.CaptureReferencePose().Returns(new[]
        {
            (app.Bone, Transform.Identity),
        });

        var result = app.Facade.ApplyReferencePose(app.Actor, receipts.Add);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(OperationReceiptState.Pending, Assert.Single(receipts).State);
        Assert.Equal(app.ActorId, receipts[0].TargetActorId);
        Assert.True(app.Imports.IsPending);
        // Ktisis memento parity: rotation and position restore, scale never
        // (PosingManager.ApplyReferencePose covers Position | Rotation).
        app.PoseFiles.Received(1).BuildImportPlan(
            Arg.Any<IReadOnlyList<ISkeleton>>(),
            Arg.Is<PoseFile>(file => file.Bones.ContainsKey("j_kao")),
            Arg.Is<PoseImportOptions>(built =>
                built.ApplyRotation
                && built.ApplyPosition
                && !built.ApplyScale
                && !built.ApplyModelTransform));
    }

    [Fact]
    public void Unreadable_reference_pose_is_a_typed_failure_without_receipts()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.Skeleton.CaptureReferencePose().Returns(
            Array.Empty<(IBone Bone, Transform Reference)>());

        var result = app.Facade.ApplyReferencePose(app.Actor, receipts.Add);

        Assert.False(result.Success);
        Assert.Contains("reference pose", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(receipts);
        Assert.False(app.Imports.IsPending);
        Assert.False(app.History.CanUndo);
    }
}
