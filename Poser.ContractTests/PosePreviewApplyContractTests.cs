using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;

namespace Poser.ContractTests;

/// <summary>
/// The pose preview's body is the one posing target that is NOT part of the
/// scene: it lives at an object index outside the GPose band, it is bound as an
/// auxiliary body so the import pipeline can reach it, and it deliberately owns
/// no <c>ActorDescriptor</c> — the snapshot is what every pane, picker, and
/// gizmo draws from and a hidden render body has no business in it.
///
/// <para>Everything downstream therefore has to admit a target the scene does
/// not contain, and the refresh has to publish bindings the scene signature
/// cannot see. Miss either and the failure is SILENT in the worst way: the
/// CharaView renders a perfectly good body standing in its idle stance while
/// every pose the user picks is dropped without a word — which is exactly what
/// shipped (build 0ca5231).</para>
/// </summary>
public sealed class PosePreviewApplyContractTests
{
    [Fact]
    public void A_preview_body_is_bound_without_ever_entering_the_scene()
    {
        using var app = new PoseImportCaptureHarness();
        var sceneActors = app.Scene.Snapshot.Actors.Count;

        // AddPreviewBody itself insists the snapshot does not move and that the
        // auxiliary half of the candidate does; both halves are the contract.
        var body = app.AddPreviewBody();

        Assert.Equal(sceneActors, app.Scene.Snapshot.Actors.Count);
        Assert.Equal(app.ActorId, app.Scene.Snapshot.Actors[0].Id);
        Assert.NotEqual(app.ActorId, body.ActorId);
        // The one question TryApplyPendingPose asks before it dispatches.
        Assert.NotNull(app.Bindings.GetActorId(body.Actor));
        Assert.Equal(body.Actor, app.Bindings.Resolve(body.ActorId).Value);
    }

    [Fact]
    public void A_steady_frame_republishes_no_auxiliary_bindings()
    {
        // The second signature may not become a reason to publish every tick:
        // an unchanged preview body has to coalesce exactly like an unchanged
        // scene, or the refresh churns at frame rate.
        using var app = new PoseImportCaptureHarness();
        app.AddPreviewBody();

        var replay = app.Bindings.RefreshCandidate();
        Assert.False(app.Bindings.AuxiliaryBindingsChanged(replay));
        app.Bindings.AbortCandidate(replay);
    }

    [Fact]
    public void A_pose_lands_on_a_preview_body_the_scene_does_not_contain()
    {
        using var app = new PoseImportCaptureHarness();
        var body = app.AddPreviewBody();
        var receipts = new List<OperationReceipt>();

        var begun = app.BeginPreviewWriteImport(body, receipts.Add);
        Assert.True(begun.Success);

        app.FirePreviewNativeAction(body);
        // The write reached the skeleton: a stack the apply pass will honour.
        Assert.Equal(1, app.PreviewStackCount(body));

        app.EndPreviewNativeBatch(body);
        app.RunNextDelay(4); // reconcile decision: a body-partial write owes none
        app.RunIfQueued(0);

        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Applied, terminal.State);
        Assert.Equal(body.ActorId, terminal.TargetActorId);
        Assert.False(app.Imports.IsPending);
        // Browsing a library is not editing: a preview import owes the user's
        // undo stack nothing.
        Assert.Null(app.History.PeekUndo());
    }
}
