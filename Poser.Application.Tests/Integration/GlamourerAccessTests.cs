using System.Reflection;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Integration;

public sealed class GlamourerAccessTests
{
    [Fact]
    public void History_restores_captured_appearance_and_collection_after_source_changes()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        const string authored = "{\"Customize\":{\"Hair\":17},\"Equipment\":{\"Head\":{\"ItemId\":42,\"Stain\":3}}}";
        port.StateResult = IntegrationValue<string>.Ok(authored);
        var saved = session.TryCaptureHistory(actor).Value!;
        port.StateResult = IntegrationValue<string>.Ok("{}");
        var replacement = ActorId.New();
        Assert.True(session.RestoreHistory(replacement, saved).Success);
        Assert.Equal(authored, port.AppliedState);
        Assert.Equal(replacement, port.AppliedActor);
        Assert.Contains(nameof(IIntegrationRuntimeFake.SetIndividualCollection), port.Calls);
        Assert.DoesNotContain(nameof(IIntegrationRuntimeFake.RevertGlamourerState), port.Calls);
        Assert.True(session.OverridesFor(replacement).DesignOwned);
        Assert.True(session.ResetDesign(replacement).Success);
        Assert.Contains(nameof(IIntegrationRuntimeFake.RestoreGlamourerState), port.Calls);
    }

    [Fact]
    public void Removal_capture_omits_an_unreadable_look_but_keeps_a_foreign_hold_refusal()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        port.Access = _ => new(GlamourerAccessKind.Unavailable, "unreadable");
        Assert.False(session.TryCaptureHistory(actor).Success);
        var removal = session.TryCaptureHistory(actor, omitUnreadableLook: true);
        Assert.True(removal.Success);
        Assert.True(removal.Value!.LookOmitted);
        Assert.Null(removal.Value.StateJson);
        port.Access = _ => GlamourerAccess.Editable;
        Assert.True(session.RestoreHistory(ActorId.New(), removal.Value).Success);
        Assert.DoesNotContain(nameof(IIntegrationRuntimeFake.ApplyGlamourerStateJson), port.Calls);
        port.Access = _ => GlamourerAccess.ForeignHeld;
        Assert.False(session.TryCaptureHistory(actor, omitUnreadableLook: true).Success);
    }

    [Fact]
    public void Foreign_hold_refuses_ordinary_commands_and_reads_without_mutation_or_unlock()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        port.Access = _ => GlamourerAccess.ForeignHeld;
        AssertRefused(session.SetItem(actor, EquipSlot.Head, 1, 0, 0));
        AssertRefused(session.SetFacewear(actor, 1));
        AssertRefused(session.SetMetaSwitch(actor, MetaSwitch.HatVisible, true));
        AssertRefused(session.SetCustomize(actor, new Dictionary<CustomizeKey, int>()));
        AssertRefused(session.ApplyStateJson(actor, "{}"));
        AssertRefused(session.RevertState(actor));
        AssertRefused(session.ApplyDesign(actor, Guid.NewGuid(), "design"));
        AssertRefused(session.OwnLook(actor));
        Assert.Equal(GlamourerAccessKind.ForeignHeld, session.SaveActorDesign(actor, "design").AppearanceRefusal);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, session.GetStateJson(actor).AppearanceRefusal);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, session.ReadWardrobe(actor).AppearanceRefusal);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, session.ReadCustomize(actor).AppearanceRefusal);
        Assert.All(port.Calls, call => Assert.Equal(nameof(IIntegrationRuntimeFake.ProbeGlamourerAccess), call));
        Assert.True(session.OpenGlamourer(actor).Success);
        Assert.False(session.OverridesFor(actor).HasAny);
    }

    [Fact]
    public void Foreign_acquisition_after_capture_preserves_baseline_until_release()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        Assert.True(session.OwnLook(actor).Success);
        port.Access = _ => GlamourerAccess.ForeignHeld;
        AssertRefused(session.ResetDesign(actor));
        Assert.Equal("baseline", session.OverridesFor(actor).Baseline.GlamourerState);
        Assert.DoesNotContain(nameof(IIntegrationRuntimeFake.RestoreGlamourerState), port.Calls);
        port.Access = _ => GlamourerAccess.Editable;
        Assert.True(session.ResetDesign(actor).Success);
        Assert.False(session.OverridesFor(actor).DesignOwned);
        Assert.Contains(nameof(IIntegrationRuntimeFake.RestoreGlamourerState), port.Calls);
    }

    private static void AssertRefused(IntegrationResult result)
    {
        Assert.False(result.Success);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }

    private static (IntegrationSelectors, RuntimeProxy) Create()
    {
        var port = DispatchProxy.Create<IIntegrationRuntimeFake, RuntimeProxy>();
        return (new IntegrationGraph(port, null!, new SessionSource()).Selectors, (RuntimeProxy)(object)port);
    }

    private sealed class SessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
    }

    public class RuntimeProxy : DispatchProxy
    {
        public Func<ActorId, GlamourerAccess> Access = _ => GlamourerAccess.Editable;
        public IntegrationValue<string> StateResult = IntegrationValue<string>.Ok("{}");
        public string? AppliedState;
        public ActorId? AppliedActor;
        public List<string> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            string name = method!.Name;
            Calls.Add(name);
            if (name is "get_CustomizePlus" or "get_Penumbra" or "get_Glamourer")
                return new IntegrationAvailability(true, "Available");
            if (name == nameof(IIntegrationRuntimeFake.ProbeBodyProfile))
                return IntegrationValue<BodyProfileProbe>.Ok(new(null, false));
            if (name == nameof(IIntegrationRuntimeFake.ProbeGlamourerAccess))
                return Access((ActorId)args![0]!);
            if (name == nameof(IIntegrationRuntimeFake.IsResolvable))
                return false;
            if (name == nameof(IIntegrationRuntimeFake.CaptureGlamourerState))
                return IntegrationValue<string>.Ok("baseline");
            if (name == nameof(IIntegrationRuntimeFake.GetActorName))
                return IntegrationValue<string>.Ok("Actor");
            if (name == nameof(IIntegrationRuntimeFake.GetGlamourerStateJson))
                return StateResult;
            if (name == nameof(IIntegrationRuntimeFake.GetCollectionAssignment))
                return IntegrationValue<CollectionAssignment>.Ok(new(Guid.Empty, "Empty", true));
            if (name == nameof(IIntegrationRuntimeFake.ApplyGlamourerStateJson))
            {
                AppliedActor = (ActorId)args![0]!;
                AppliedState = (string)args[1]!;
            }
            if (name == nameof(IIntegrationRuntimeFake.AddDesign))
                return IntegrationValue<Guid>.Ok(Guid.NewGuid());
            if (method.ReturnType == typeof(IntegrationResult))
                return IntegrationResult.Ok();
            throw new NotSupportedException(name);
        }
    }
}
