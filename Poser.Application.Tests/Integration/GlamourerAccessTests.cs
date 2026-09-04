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
        Assert.All(port.Calls, call => Assert.Equal(nameof(IIntegrationRuntimePort.ProbeGlamourerAccess), call));
        Assert.True(session.OpenGlamourer(actor).Success);
        Assert.False(session.OverridesFor(actor).HasAny);
    }

    [Fact]
    public void Commands_probe_again_after_cached_success_and_recover_after_release()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        Assert.True(session.AppearanceAccess(actor).CanEdit);
        port.Access = _ => GlamourerAccess.ForeignHeld;
        AssertRefused(session.SetItem(actor, EquipSlot.Head, 1, 0, 0));
        Assert.DoesNotContain(nameof(IIntegrationRuntimePort.SetItem), port.Calls);
        port.Access = _ => GlamourerAccess.Editable;
        Assert.True(session.SetItem(actor, EquipSlot.Head, 1, 0, 0).Success);
        Assert.Contains(nameof(IIntegrationRuntimePort.SetItem), port.Calls);
    }

    [Fact]
    public void Actor_and_generation_switches_never_reuse_access()
    {
        var (session, port) = Create();
        var old = ActorId.New();
        var replacement = old.NextGeneration();
        var other = ActorId.New();
        port.Access = id => id == old ? GlamourerAccess.ForeignHeld : GlamourerAccess.Editable;
        AssertRefused(session.RevertState(old));
        Assert.True(session.RevertState(replacement).Success);
        Assert.True(session.RevertState(other).Success);
        port.Access = id => id == replacement
            ? new(GlamourerAccessKind.Unavailable, "Actor generation no longer resolves.")
            : GlamourerAccess.Editable;
        Assert.Equal(GlamourerAccessKind.Unavailable, session.RevertState(replacement).AppearanceRefusal);
    }

    [Fact]
    public void Own_hold_and_general_failures_are_not_foreign_or_bypassed()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        port.Access = _ => GlamourerAccess.PoserHeld;
        Assert.Equal(GlamourerAccessKind.PoserHeld, session.RevertState(actor).AppearanceRefusal);
        port.Access = _ => new(GlamourerAccessKind.Unavailable, "Read failed");
        Assert.Equal(GlamourerAccessKind.Unavailable, session.RevertState(actor).AppearanceRefusal);
        Assert.DoesNotContain(nameof(IIntegrationRuntimePort.RevertGlamourerState), port.Calls);
        port.Access = _ => GlamourerAccess.Editable;
        port.WriteResult = IntegrationPortResult.Fail("Native failure");
        var failed = session.RevertState(actor);
        Assert.False(failed.Success);
        Assert.Null(failed.AppearanceRefusal);
        Assert.Equal("Native failure", failed.Detail);
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
        Assert.DoesNotContain(nameof(IIntegrationRuntimePort.RestoreGlamourerState), port.Calls);
        port.Access = _ => GlamourerAccess.Editable;
        Assert.True(session.ResetDesign(actor).Success);
        Assert.False(session.OverridesFor(actor).DesignOwned);
        Assert.Contains(nameof(IIntegrationRuntimePort.RestoreGlamourerState), port.Calls);
    }

    [Fact]
    public void By_name_foreign_refusal_retains_baseline_and_name_for_retry()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        Assert.True(session.OwnLook(actor).Success);
        port.WriteResult = IntegrationPortResult.Refused(GlamourerAccess.ForeignHeld);
        Assert.False(session.ResetActor(actor).Success);
        Assert.True(session.OverridesFor(actor).DesignOwned);
        Assert.Equal("Actor", session.OverridesFor(actor).DesignActorName);
        Assert.Equal("baseline", session.OverridesFor(actor).Baseline.GlamourerState);
        port.WriteResult = IntegrationPortResult.Ok();
        Assert.True(session.ResetActor(actor).Success);
        Assert.False(session.OverridesFor(actor).HasAny);
        Assert.Equal(2, port.Calls.Count(c => c == nameof(IIntegrationRuntimePort.RestoreGlamourerStateByName)));
    }

    [Fact]
    public void Save_rechecks_after_read_before_adding_design()
    {
        var (session, port) = Create();
        port.AfterStateRead = () => port.Access = _ => GlamourerAccess.ForeignHeld;
        var saved = session.SaveActorDesign(ActorId.New(), "design");
        Assert.Equal(GlamourerAccessKind.ForeignHeld, saved.AppearanceRefusal);
        Assert.DoesNotContain(nameof(IIntegrationRuntimePort.AddDesign), port.Calls);
    }

    [Fact]
    public void Native_refusals_after_editable_probe_keep_their_type()
    {
        var (session, port) = Create();
        var actor = ActorId.New();
        port.CaptureResult = IntegrationValue<string>.Refused(GlamourerAccess.ForeignHeld);
        AssertRefused(session.OwnLook(actor));
        AssertRefused(session.ApplyDesign(actor, Guid.NewGuid(), "design"));
        port.StateResult = IntegrationValue<string>.Refused(GlamourerAccess.ForeignHeld);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, session.SaveActorDesign(actor, "design").AppearanceRefusal);
        port.CaptureResult = IntegrationValue<string>.Ok("baseline");
        port.WriteResult = IntegrationPortResult.Refused(GlamourerAccess.ForeignHeld);
        AssertRefused(session.ApplyDesign(actor, Guid.NewGuid(), "design"));
        Assert.True(session.OwnLook(actor).Success);
        AssertRefused(session.ResetDesign(actor));
        Assert.True(session.OverridesFor(actor).DesignOwned);
    }

    [Fact]
    public void Unlocked_design_save_succeeds()
    {
        var (session, port) = Create();
        Assert.True(session.SaveActorDesign(ActorId.New(), "design").Success);
        Assert.Contains(nameof(IIntegrationRuntimePort.AddDesign), port.Calls);
    }

    private static void AssertRefused(IntegrationResult result)
    {
        Assert.False(result.Success);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }

    private static (ActorIntegrationSession, RuntimeProxy) Create()
    {
        var port = DispatchProxy.Create<IIntegrationRuntimePort, RuntimeProxy>();
        return (new ActorIntegrationSession(port, null!, new SessionSource()), (RuntimeProxy)(object)port);
    }

    private sealed class SessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
    }

    public class RuntimeProxy : DispatchProxy
    {
        public Func<ActorId, GlamourerAccess> Access = _ => GlamourerAccess.Editable;
        public IntegrationValue<string> CaptureResult = IntegrationValue<string>.Ok("baseline");
        public IntegrationValue<string> StateResult = IntegrationValue<string>.Ok("{}");
        public IntegrationPortResult WriteResult = IntegrationPortResult.Ok();
        public Action? AfterStateRead;
        public List<string> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            string name = method!.Name;
            Calls.Add(name);
            if (name == nameof(IIntegrationRuntimePort.ProbeGlamourerAccess))
                return Access((ActorId)args![0]!);
            if (name == nameof(IIntegrationRuntimePort.IsResolvable))
                return false;
            if (name == nameof(IIntegrationRuntimePort.CaptureGlamourerState))
                return CaptureResult;
            if (name == nameof(IIntegrationRuntimePort.GetActorName))
                return IntegrationValue<string>.Ok("Actor");
            if (name == nameof(IIntegrationRuntimePort.GetGlamourerStateJson))
            {
                AfterStateRead?.Invoke();
                return StateResult;
            }
            if (name == nameof(IIntegrationRuntimePort.AddDesign))
                return IntegrationValue<Guid>.Ok(Guid.NewGuid());
            if (method.ReturnType == typeof(IntegrationPortResult))
                return WriteResult;
            throw new NotSupportedException(name);
        }
    }
}
