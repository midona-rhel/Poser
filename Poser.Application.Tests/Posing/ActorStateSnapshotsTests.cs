using System.Numerics;
using System.Reflection;
using Poser.Application.Appearance;
using Poser.Application.Gaze;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Posing;

public sealed class ActorStateSnapshotsTests
{
    [Fact]
    public async Task Restore_waits_for_redraw_and_pose_before_presentation_gaze_and_expression()
    {
        var f = new Fixture();
        Assert.True(f.Presentation.SetTint(f.Actor, PresentationModel.Character, new Vector4(1, 0, 0, 1)).Success);
        var saved = f.States.Capture(f.Actor);
        Assert.True(saved.Success, saved.Detail);
        Assert.True(f.Presentation.ResetActor(f.Actor).Success);
        f.Model = 99;
        f.Look = "other look";
        f.Profile = "other profile";
        f.Weight = 0;
        f.Events.Clear();
        var completed = new TaskCompletionSource<GestureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.States.Restore(saved.Value!, () => true, TestContext.Current.CancellationToken, completed.SetResult);
        await f.RedrawEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("authored look", f.Look);
        Assert.Equal("authored profile", f.Profile);
        Assert.Equal(42, f.Model);
        Assert.DoesNotContain("pose", f.Events);
        f.Redraw.SetResult(IntegrationPortResult.Ok());
        await f.PoseEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("tint", f.Events);
        Assert.DoesNotContain("expression", f.Events);
        Assert.False(completed.Task.IsCompleted);
        f.FinishPose!(true);
        Assert.True((await completed.Task.WaitAsync(TestContext.Current.CancellationToken)).Success);
        Assert.Equal(new Vector4(1, 0, 0, 1), f.Tint);
        Assert.Equal(.7f, f.Weight);
        Assert.Contains("gaze target", f.Events);
        Assert.True(f.Events.IndexOf("pose") < f.Events.IndexOf("tint"));
        Assert.True(f.Events.IndexOf("tint") < f.Events.IndexOf("expression"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_replaced_session_after_redraw_never_restores_pose(bool replaceSession)
    {
        var f = new Fixture();
        var saved = f.States.Capture(f.Actor).Value!;
        var completed = new TaskCompletionSource<GestureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.States.Restore(saved, () => true, TestContext.Current.CancellationToken, completed.SetResult);
        await f.RedrawEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (replaceSession) f.ActiveSessionGeneration = SessionGeneration.New();
        f.Redraw.SetResult(replaceSession ? IntegrationPortResult.Ok() : IntegrationPortResult.Fail("Redraw failed"));
        Assert.False((await completed.Task.WaitAsync(TestContext.Current.CancellationToken)).Success);
        Assert.DoesNotContain("pose", f.Events);
    }

    [Fact]
    public void Required_appearance_capture_failure_is_not_represented_as_empty_state()
    {
        var f = new Fixture { CannotReadLook = true };
        var result = f.States.Capture(f.Actor);
        Assert.False(result.Success);
        Assert.Contains("look unreadable", result.Detail);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void Pose_capture_exception_is_a_refusal_before_any_mutation()
    {
        var f = new Fixture { ThrowPoseCapture = true };
        var result = f.States.Capture(f.Actor);
        Assert.False(result.Success);
        Assert.Contains("Pose unavailable", result.Detail);
        Assert.Empty(f.Events);
    }

    private sealed class Fixture : ISessionGenerationSource, IPoseSnapshotPort
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; } = SessionGeneration.New();
        public ActorId Actor { get; } = ActorId.New();
        public ActorStateSnapshots States { get; }
        public ActorPresentationSession Presentation { get; }
        public List<string> Events { get; } = [];
        public TaskCompletionSource<bool> RedrawEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IntegrationPortResult> Redraw { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PoseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<bool>? FinishPose;
        public string Look = "authored look", Profile = "authored profile";
        public int Model = 42;
        public float Weight = .7f;
        public Vector4 Tint = Vector4.One;
        public bool CannotReadLook;
        public bool ThrowPoseCapture;
        private readonly Guid _profile = Guid.NewGuid();

        public Fixture()
        {
            var scene = new SceneSession(new SelectionSession());
            scene.TryRefresh(new SceneSnapshot(1, [new ActorDescriptor(Actor, "actor", [])], [], [], []));
            var integrationPort = Port<IIntegrationRuntimePort>(Integration);
            var appearance = new ActorIntegrationSession(integrationPort, null!, this);
            Presentation = new(Port<IPresentationRuntimePort>((method, args) =>
            {
                switch (method.Name)
                {
                    case "IsSupported": return true;
                    case "Read": return new PresentationReading(1, Tint, null, null, default);
                    case "SetTint":
                    case "RestoreTint": Tint = (Vector4)args[2]!; Events.Add("tint"); return PresentationPortResult.Ok();
                    case "SuspendColors": return null;
                    case "ClearOwned": return null;
                    default: throw new InvalidOperationException(method.Name);
                }
            }));
            var models = new ActorModelIdSession(Port<IModelIdRuntimePort>((method, args) =>
            {
                if (method.Name == "Read") return (int?)Model;
                Model = (int)args[1]!; Events.Add("model"); return PresentationPortResult.Ok();
            }));
            var gaze = new GazeSession(new ValueJournal(new TransformHistory()), Port<IGazeRuntimePort>((method, args) =>
            {
                if (method.Name == "get_IsAvailable") return true;
                if (method.Name == "Read") return new GazeReading(new(GazeTargetMode.Entity, GazeTargetType.Eyes,
                    Vector3.One, Vector3.One, Vector3.One, Vector3.One, true, false, false), Actor, true, false);
                if (method.Name == "SetTarget") Events.Add("gaze target");
                return GazeResult.Ok();
            }));
            var expressions = Port<IExpressionRuntimePort>((method, args) => method.Name switch
            {
                "get_IsAvailable" or "HasActor" => true,
                "GetUnits" => new List<(string, string, bool, bool)> { ("smile", "Smile", false, true) },
                "GetWeight" => Weight,
                "Write" => WriteExpression(args),
                _ => throw new InvalidOperationException(method.Name),
            });
            States = new(scene, this, new(() => this), appearance, Presentation, models, gaze, expressions, integrationPort);
        }

        private ValueWriteResult WriteExpression(object?[] args)
        {
            var weights = (IReadOnlyList<(string, float)>)args[1]!;
            Weight = weights.Single().Item2;
            Events.Add("expression");
            return ValueWriteResult.Ok();
        }

        private object? Integration(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case "OnFrameworkThread":
                    var value = ((Delegate)args[0]!).DynamicInvoke();
                    return typeof(Task).GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(method.GetGenericArguments()).Invoke(null, [value]);
                case "get_Penumbra":
                case "get_Glamourer":
                case "get_CustomizePlus": return new IntegrationAvailability(true, "");
                case "IsResolvable": return true;
                case "ProbeGlamourerAccess": return GlamourerAccess.Editable;
                case "GetActorName": return IntegrationValue<string>.Ok("actor");
                case "GetGlamourerStateJson": return CannotReadLook
                    ? IntegrationValue<string>.Fail("look unreadable") : IntegrationValue<string>.Ok(Look);
                case "CaptureGlamourerState": return IntegrationValue<string>.Ok(Look);
                case "GetCollectionAssignment": return IntegrationValue<CollectionAssignment>.Ok(new(Guid.Empty, "Empty", true));
                case "SetIndividualCollection": Events.Add("collection"); return IntegrationPortResult.Ok();
                case "ProbeBodyProfile": return IntegrationValue<BodyProfileProbe>.Ok(new(_profile, true));
                case "GetBodyProfileJson": return IntegrationValue<string>.Ok(Profile);
                case "ApplyTemporaryBodyProfile":
                    Profile = (string)args[1]!; Events.Add("profile"); return IntegrationValue<Guid>.Ok(Guid.NewGuid());
                case "ApplyGlamourerStateJson":
                    Look = (string)args[1]!; Events.Add("look"); return IntegrationPortResult.Ok();
                case "RedrawAndWait": RedrawEntered.TrySetResult(true); return Redraw.Task;
                default: throw new InvalidOperationException(method.Name);
            }
        }

        public ActorSnapshot? Capture(Guid lineage) => ThrowPoseCapture
            ? throw new InvalidOperationException("Pose unavailable") : new(lineage, new object(), []);
        public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
        {
            Events.Add("pose");
            FinishPose = finished;
            PoseEntered.TrySetResult(true);
            return true;
        }
    }

    private static T Port<T>(Func<MethodInfo, object?[], object?> handle) where T : class
    {
        var port = DispatchProxy.Create<T, PortProxy>();
        ((PortProxy)(object)port).Handle = handle;
        return port;
    }

    public class PortProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handle = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handle(method!, args ?? []);
    }
}
