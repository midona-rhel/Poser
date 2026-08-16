using System.Numerics;
using NSubstitute;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
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
    public void Structural_selection_mirror_group_relative_and_portable_paths_stay_intact()
    {
        using var import = new PoseImportCaptureHarness();

        var receipts = new List<OperationReceipt>();
        var refusal = import.Facade.ImportPose(
            import.Actor, new PoseFile(), new PoseImportOptions(),
            "structural selection", receipts.Add, Array.Empty<BoneId>());
        Assert.False(refusal.Success);
        Assert.Empty(receipts);


        // Selection scope is an explicit, read-only view rather than a list
        // that a caller can mutate behind the session's back.
        var selected = SelectionId.ForActor(import.ActorId);
        var scope = new SelectionScope(selected);
        var selectedView = Assert.IsAssignableFrom<IList<SelectionId>>(
            scope.Selected);
        Assert.Throws<NotSupportedException>(() => selectedView.Clear());
        Assert.Equal(selected, scope.Primary);

        // A selected head admits its same-slot descendant through the real
        // plan builder, while the structural filter remains slot-qualified.
        var service = RealFileService();
        var plan = service.BuildImportPlan(
            new[] { import.Skeleton },
            FileWith("j_kao", "j_mab_l"),
            new PoseImportOptions
            {
                ApplyRotation = true,
                ApplyBody = true,
                ApplyFace = true,
                BoneFilter = new HashSet<(PoseSlot, string)>
                    { (PoseSlot.Character, "j_kao") },
                FilterIncludesDescendants = true,
            });
        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_kao");
        Assert.Contains(plan.Writes, write => write.Bone.BoneName == "j_mab_l");

        var delta = TransformDelta.Identity with
        {
            Translation = new Vector3(2f, 3f, 4f),
        };
        Assert.Equal(new Vector3(-2f, 3f, 4f), TransformMath.Mirror(delta).Translation);

        var relative = TransformMath.RelativeToPrimary(
            TransformDelta.Identity with
            {
                Rotation = Quaternion.CreateFromAxisAngle(
                    Vector3.UnitY, MathF.PI / 6f),
            },
            Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
        Assert.True(MathF.Abs(relative.Rotation.Length() - 1f) < 1e-4f);

        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var group = new TransformApplicationHarness();
        group.Scene.Refresh(TestScenes.ActorsScene(
            first.Actor!.Value, second.Actor!.Value));
        group.Runtime.Seed(TestStates.At(first, 0f));
        group.Runtime.Seed(TestStates.At(second, 4f));
        var begun = group.Gestures.Begin(new BeginTransformGesture(
            new[] { first, second },
            TransformOperation.Rotate,
            TransformSpace.World,
            PivotMode.Centroid,
            Description: "structural group"));
        Assert.True(begun.Success, begun.Detail);
        Assert.Equal(new Vector3(2f, 0f, 0f), group.Gestures.ActivePivot);

        var portablePath = new BonePath("root", import.Bone.BoneName);
        var portable = new PortablePose(new[]
        {
            new PortableBoneEntry(
                PortableBoneKey.From(import.Bone, portablePath),
                new BonePose(),
                import.Bone.BoneIndex),
        });
        var match = portable.Match(new[]
        {
            PortableBoneTarget.From(import.Bone, portablePath),
        });
        Assert.True(match.Success);
        Assert.Single(match.Matches);
        Assert.Equal(import.Bone, match.Matches[0].Target.Bone);
    }

    // ── Direct selection bypasses the mode gates (Ktisis parity) ─────────




    [Fact]
    public void Selected_import_arms_one_transaction_with_anchor_and_mode_bypass()
    {
        using var app = new PoseImportCaptureHarness();
        app.SetNextPlan(PlanWithOneWrite(app));
        var receipts = new List<OperationReceipt>();
        var options = new PoseImportOptions
        {
            ApplyPosition = true,
            ApplyRotation = true,
            ApplyScale = true,
            ApplyBody = false,
            ApplyFace = false,
            ApplyMainHand = false,
            ApplyOffHand = false,
            ApplyProp = false,
            ApplyOrnament = false,
            AnchorSelectedPositions = true,
            FilterIncludesDescendants = true,
        };

        var result = app.Facade.ImportPose(
            app.Actor,
            new PoseFile(),
            options,
            "selected import",
            receipts.Add,
            new[] { SnapshotBone(app) });

        Assert.True(result.Success, result.Detail);
        PoseImportOptions? built = null;
        app.PoseFiles.Received(1).BuildImportPlan(
            Arg.Any<IReadOnlyList<ISkeleton>>(),
            Arg.Any<PoseFile>(),
            Arg.Do<PoseImportOptions>(value => built = value));
        Assert.NotNull(built);
        Assert.True(IsReducedHeadFilter(built!));
        Assert.True(built.AnchorSelectedPositions);
        Assert.False(built.ApplyBody);
        Assert.False(built.ApplyFace);

        app.FireRegisteredNativeAction();
        app.EndRegisteredNativeBatch();
        app.RunNextDelay(4);
        app.RunIfQueued(0);

        var receipt = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Applied, receipt.State);
        Assert.Equal(app.ActorId, receipt.TargetActorId);
        Assert.False(app.Imports.IsPending);
        Assert.Null(app.History.PeekUndo());
    }

    [Fact]
    public void Real_plan_applies_direct_selection_through_disabled_modes_and_anchors_position()
    {
        using var app = new PoseImportCaptureHarness();
        var file = FileWith("j_kao", "j_mab_l");
        file.Bones["j_kao"].Position = new Vector3(4f, 5f, 6f);
        file.Bones["j_mab_l"].Position = new Vector3(7f, 8f, 9f);
        file.MainHand["n_hara"] = new PoseFile.BoneData
        {
            Position = new Vector3(10f, 11f, 12f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var options = new PoseImportOptions
        {
            ApplyPosition = true,
            ApplyRotation = true,
            ApplyScale = true,
            ApplyBody = false,
            ApplyFace = false,
            ApplyMainHand = false,
            BoneFilter = new HashSet<(PoseSlot, string)>
            {
                (PoseSlot.Character, "j_kao"),
                (PoseSlot.MainHand, "n_hara"),
            },
            FilterIncludesDescendants = true,
            AnchorSelectedPositions = true,
        };

        var plan = RealFileService().BuildImportPlan(
            new[] { app.Skeleton, app.WeaponSkeleton }, file, options);

        Assert.Contains(
            plan.Writes,
            write => write.Bone == app.Bone &&
                write.Components == (TransformComponents.Rotation |
                    TransformComponents.Scale));
        Assert.DoesNotContain(plan.Writes, write => write.Bone == app.FaceBone);
        Assert.Contains(
            plan.Writes,
            write => write.Bone == app.WeaponBone &&
                write.Components == (TransformComponents.Rotation |
                    TransformComponents.Scale));
    }

    // ── Anchor positions (Ktisis "Anchor group positions") ───────────────





    [Fact]
    public void Reference_pose_uses_the_same_transaction_and_never_writes_scale()
    {
        using var app = new PoseImportCaptureHarness();
        app.Skeleton.CaptureReferencePose().Returns(
            new[]
            {
                (app.Bone, new Transform
                {
                    Position = new Vector3(2f, 3f, 4f),
                    Rotation = Quaternion.Identity,
                    Scale = new Vector3(9f, 9f, 9f),
                }),
            });
        app.SetNextPlan(PlanWithOneWrite(app));
        var receipts = new List<OperationReceipt>();

        var result = app.Facade.ApplyReferencePose(app.Actor, receipts.Add);

        Assert.True(result.Success, result.Detail);
        PoseImportOptions? built = null;
        app.PoseFiles.Received(1).BuildImportPlan(
            Arg.Any<IReadOnlyList<ISkeleton>>(),
            Arg.Any<PoseFile>(),
            Arg.Do<PoseImportOptions>(value => built = value));
        Assert.NotNull(built);
        Assert.True(built.ApplyRotation);
        Assert.True(built.ApplyPosition);
        Assert.False(built.ApplyScale);
        Assert.True(built.ApplyBody);
        Assert.True(built.ApplyFace);
        Assert.False(built.ApplyMainHand);
        Assert.False(built.ApplyOffHand);
        Assert.False(built.ApplyProp);
        Assert.False(built.ApplyOrnament);

        app.FireRegisteredNativeAction();
        app.EndRegisteredNativeBatch();
        app.RunNextDelay(4);
        app.RunIfQueued(0);

        var receipt = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Applied, receipt.State);
        Assert.Equal(app.ActorId, receipt.TargetActorId);
    }

}
