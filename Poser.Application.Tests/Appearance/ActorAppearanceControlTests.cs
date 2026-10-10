using System.Reflection;
using Poser.Application.Posing;
using Poser.Domain;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Application.Appearance;
using Poser.Application.Integration;
using Poser.Application.Tests.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Appearance;

public sealed class ActorAppearanceControlTests
{
    [Fact]
    public void Body_profile_reset_undo_restores_contents_after_temporary_id_is_deleted()
    {
        var f = new Fixture();
        Assert.True(f.Control.SetBodyProfile(f.Actor, f.Runtime.SavedProfile, "Body").Success);
        var oldId = f.Runtime.ActiveProfile;
        Assert.True(f.Control.ResetBodyProfile(f.Actor).Success);
        Assert.Null(f.Runtime.ActiveProfile);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Replay(step, true);
        Assert.NotEqual(oldId, f.Runtime.ActiveProfile);
        Assert.Equal("body contents", f.Runtime.ProfileJson);
        Replay(step, false);
        Assert.Null(f.Runtime.ActiveProfile);
        Replay(step, true);
        Assert.Equal("body contents", f.Runtime.ProfileJson);
    }

    [Fact]
    public void Collection_reset_and_history_restore_assignment_and_inheritance()
    {
        var f = new Fixture();
        var chosen = Guid.NewGuid();
        Assert.True(f.Control.SetCollection(f.Actor, chosen, "Chosen").Success);
        var apply = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Replay(apply, true);
        Assert.False(f.Runtime.Collection.HasIndividualAssignment);
        Replay(apply, false);
        Assert.Equal(chosen, f.Runtime.Collection.EffectiveId);
        Assert.True(f.Control.ResetCollection(f.Actor).Success);
        var reset = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.False(f.Runtime.Collection.HasIndividualAssignment);
        Replay(reset, true);
        Assert.Equal(chosen, f.Runtime.Collection.EffectiveId);
        Replay(reset, false);
        Assert.False(f.Runtime.Collection.HasIndividualAssignment);
    }

    private static void Replay(JournalStep step, bool undo)
    {
        Assert.True(undo ? step.Undo() : step.Redo());
        GestureResult? result = null;
        step.CompleteReplay!(undo, () => true, TestContext.Current.CancellationToken, r => result = r);
        Assert.True(result?.Success, result?.Detail);
    }

    private sealed class Fixture : ISessionGenerationSource, IActorStateSnapshots, IModelIdRuntimePort
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
        public ActorId Actor { get; } = ActorId.New();
        public TransformHistory History { get; } = new();
        public RuntimeProxy Runtime { get; }
        public IActorAppearanceControl Control { get; }
        private readonly IntegrationSelectors _integration;
        private readonly ActorModelIdSession _models;
        public int Model;
        public bool RefuseModel;
        public Fixture()
        {
            var port = DispatchProxy.Create<IIntegrationRuntimeFake, RuntimeProxy>();
            Runtime = (RuntimeProxy)(object)port;
            _integration = new IntegrationGraph(port, null!, this).Selectors;
            _models = new(this);
            Control = new ActorAppearanceControl(_integration, _models,
                new(History, this, new ValueJournal(History)));
        }
        public IntegrationValue<ActorStateSnapshot> Capture(ActorId actor)
        {
            var look = _integration.TryCaptureHistory(actor);
            return look.Success && look.Value is { } captured
                ? IntegrationValue<ActorStateSnapshot>.Ok(new(actor, ActiveSessionGeneration!.Value,
                    new(actor.LogicalId, new object(), []),
                    new(Model, captured, PresentationOverrides.None, null, null)))
                : IntegrationValue<ActorStateSnapshot>.Fail(look.Detail!);
        }
        public void Restore(ActorStateSnapshot snapshot, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed)
        {
            var result = _integration.RestoreHistory(snapshot.Actor, snapshot.Properties.Appearance);
            if (result.Success) _models.Apply(snapshot.Actor, snapshot.Properties.ModelId);
            completed(result.Success ? GestureResult.Ok() : GestureResult.Fail(result.Detail!));
        }
        public void WaitForReset(ActorId actor, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed) => throw new NotSupportedException();
        public int? Read(ActorId actor) => actor == Actor ? Model : null;
        public Outcome Write(ActorId actor, int value)
        {
            if (RefuseModel || actor != Actor) return Outcome.Fail("Refused");
            Model = value;
            return Outcome.Ok();
        }
    }

    public class RuntimeProxy : DispatchProxy
    {
        public Guid SavedProfile = Guid.NewGuid();
        public Guid? ActiveProfile;
        public string? ProfileJson;
        public CollectionAssignment Collection = new(Guid.Empty, "Inherited", false);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Penumbra":
                case "get_CustomizePlus": return new IntegrationAvailability(true, "");
                case "get_Glamourer": return new IntegrationAvailability(false, "");
                case nameof(IIntegrationRuntimeFake.GetBodyProfileJson):
                    return (Guid)args![0]! == SavedProfile
                        ? IntegrationValue<string>.Ok("body contents")
                        : IntegrationValue<string>.Fail("Temporary id is not a saved profile");
                case nameof(IIntegrationRuntimeFake.ProbeBodyProfile):
                    return IntegrationValue<BodyProfileProbe>.Ok(new(ActiveProfile, false));
                case nameof(IIntegrationRuntimeFake.ApplyTemporaryBodyProfile):
                    ProfileJson = (string)args![1]!;
                    ActiveProfile = Guid.NewGuid();
                    return IntegrationValue<Guid>.Ok(ActiveProfile.Value);
                case nameof(IIntegrationRuntimeFake.DeleteTemporaryBodyProfileById):
                    Assert.Equal(ActiveProfile, (Guid)args![0]!);
                    ActiveProfile = null;
                    ProfileJson = null;
                    return IntegrationResult.Ok();
                case nameof(IIntegrationRuntimeFake.GetCollectionAssignment):
                    return IntegrationValue<CollectionAssignment>.Ok(Collection);
                case nameof(IIntegrationRuntimeFake.SetIndividualCollection):
                    Collection = new((Guid)args![1]!, "Chosen", true);
                    return IntegrationResult.Ok();
                case nameof(IIntegrationRuntimeFake.RestoreCollection):
                    Collection = new(Guid.Empty, "Inherited", false);
                    return IntegrationResult.Ok();
                case nameof(IIntegrationRuntimeFake.RequestRedraw):
                    return IntegrationResult.Ok();
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
