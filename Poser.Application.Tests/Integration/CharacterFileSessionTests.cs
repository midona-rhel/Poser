using System.Reflection;
using Poser.Application.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Documents.Appearance;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Integration;

public sealed class CharacterFileSessionTests
{
    [Fact]
    public void Invalid_file_does_not_spawn_and_pending_spawn_is_not_overwritten()
    {
        var f = new Fixture();
        f.Files.Document = null;
        Assert.Null(f.Control.Spawn("invalid.chara").Handle);
        Assert.Equal(0, f.Creation.Created);
        f.Files.Document = "first";
        Assert.NotNull(f.Control.Spawn("valid.chara").Handle);
        Assert.Null(f.Control.Spawn("another.chara").Handle);
        Assert.Equal(1, f.Creation.Created);
    }

    [Fact]
    public void Ready_spawn_uses_frozen_document_once_and_history_restores_its_exact_actor()
    {
        var f = new Fixture();
        var handle = f.Control.Spawn("actor.chara").Handle;
        Assert.NotNull(handle);
        Assert.Null(f.Control.Advance());
        f.Files.Document = "changed on disk";
        f.Creation.Ready = true;
        Assert.True(f.Control.Advance()!.Value.Success);
        Assert.Equal("chosen document", f.Runtime.State);
        Assert.True(f.Creation.RequiredPose);
        Assert.Null(f.Control.Advance());
        Assert.Single(f.Runtime.Writes);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Replay(step, true);
        Assert.Equal("original", f.Runtime.State);
        Replay(step, false);
        Assert.Equal("chosen document", f.Runtime.State);
        Assert.All(f.Runtime.Writes, actor => Assert.Equal(f.Creation.Actor, actor));
    }

    [Fact]
    public void Session_change_and_timeout_never_apply_a_delayed_file()
    {
        var f = new Fixture();
        Assert.NotNull(f.Control.Spawn("actor.chara").Handle);
        f.ActiveSessionGeneration = SessionGeneration.New();
        f.Creation.Ready = true;
        Assert.Null(f.Control.Advance());
        Assert.Empty(f.Runtime.Writes);
        f.Creation.Ready = false;
        Assert.NotNull(f.Control.Spawn("actor.chara").Handle);
        f.Clock.Seconds = 31;
        Assert.False(f.Control.Advance()!.Value.Success);
        f.Creation.Ready = true;
        Assert.Null(f.Control.Advance());
        Assert.Empty(f.Runtime.Writes);
        Assert.False(f.History.CanUndo);
    }

    private static void Replay(JournalStep step, bool undo)
    {
        Assert.True(undo ? step.Undo() : step.Redo());
        GestureResult? result = null;
        step.CompleteReplay!(undo, () => true, TestContext.Current.CancellationToken, r => result = r);
        Assert.True(result?.Success, result?.Detail);
    }

    private sealed class Fixture : ISessionGenerationSource, IActorStateSnapshots
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; } = SessionGeneration.New();
        public TransformHistory History { get; } = new();
        public Files Files { get; } = new();
        public Clock Clock { get; } = new();
        public RuntimeProxy Runtime { get; }
        public CreationProxy Creation { get; }
        public CharacterFileSession Control { get; }
        private readonly ActorIntegrationSession _integration;
        public Fixture()
        {
            var port = DispatchProxy.Create<IIntegrationRuntimePort, RuntimeProxy>();
            Runtime = (RuntimeProxy)(object)port;
            var creation = DispatchProxy.Create<ISceneCreation, CreationProxy>();
            Creation = (CreationProxy)(object)creation;
            Creation.Session = () => ActiveSessionGeneration!.Value;
            var integration = _integration = new ActorIntegrationSession(port, null!, this);
            Control = new(integration, Files, new(History, this, new(), new ValueJournal(History)), creation, this, Clock);
        }
        public IntegrationValue<ActorStateSnapshot> Capture(ActorId actor) =>
            IntegrationValue<ActorStateSnapshot>.Ok(new(actor, ActiveSessionGeneration!.Value,
                new(actor.LogicalId, new object(), []),
                new(0, new(Runtime.State, null, null, null, null), PresentationOverrides.None, null, null)));
        public void Restore(ActorStateSnapshot snapshot, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed)
        {
            var result = _integration.RestoreHistory(snapshot.Actor, snapshot.Properties.Appearance);
            completed(result.Success ? GestureResult.Ok() : GestureResult.Fail(result.Detail!));
        }
        public void WaitForReset(ActorId actor, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed) => throw new NotSupportedException();
    }

    private sealed class Files : ICharacterAppearanceFiles
    {
        public string? Document = "chosen document";
        public IntegrationValue<string> Read(string path) => Document is { } text
            ? IntegrationValue<string>.Ok(text) : IntegrationValue<string>.Fail("Invalid file");
        public IntegrationValue<string> BuildRequest(string currentState, string characterFile) =>
            IntegrationValue<string>.Ok(characterFile);
    }

    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    public class CreationProxy : DispatchProxy
    {
        public Func<SessionGeneration> Session = null!;
        public ActorId Actor = ActorId.New();
        public bool Ready, RequiredPose;
        public int Created;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(ISceneCreation.CreateActor):
                    Created++;
                    return new SceneCreationResult(new(Session(), SceneEntityKind.Actor));
                case nameof(ISceneCreation.Resolve):
                    RequiredPose = (bool)args![1]!;
                    return Ready ? SelectionId.ForActor(Actor) : null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public class RuntimeProxy : DispatchProxy
    {
        public string State = "original";
        public List<ActorId> Writes = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IIntegrationRuntimePort.ProbeGlamourerAccess): return GlamourerAccess.Editable;
                case nameof(IIntegrationRuntimePort.GetGlamourerStateJson):
                case nameof(IIntegrationRuntimePort.CaptureGlamourerState): return IntegrationValue<string>.Ok(State);
                case nameof(IIntegrationRuntimePort.GetActorName): return IntegrationValue<string>.Ok("Actor");
                case nameof(IIntegrationRuntimePort.ApplyGlamourerStateJson):
                    Writes.Add((ActorId)args![0]!);
                    State = (string)args[1]!;
                    return IntegrationPortResult.Ok();
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
