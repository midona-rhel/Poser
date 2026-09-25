using System.Reflection;
using Poser.Application.Appearance;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Appearance;

public sealed class AppearanceControlTests
{
    [Theory]
    [InlineData("item")]
    [InlineData("dye")]
    [InlineData("facewear")]
    [InlineData("switch")]
    [InlineData("outfit")]
    public void Equipment_inverse_refusals_preserve_failure_and_can_retry(string action)
    {
        var f = new Fixture();
        var before = f.Runtime.ReadWardrobe();
        Assert.True(Edit(f, action).Success);
        var after = f.Runtime.ReadWardrobe();
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        f.Runtime.Refuse = true;
        Assert.False(step.Undo());
        Assert.True(step.RetainOnFailure);
        Assert.Equal("Write refused", step.FailureDetail!());
        EqualWardrobe(after, f.Runtime.ReadWardrobe());
        f.Runtime.Refuse = false;
        Assert.True(step.Undo());
        EqualWardrobe(before, f.Runtime.ReadWardrobe());
        Assert.True(step.Redo());
        EqualWardrobe(after, f.Runtime.ReadWardrobe());
    }

    [Fact]
    public void Partial_outfit_remains_undoable_and_restoration_stops_on_refusal()
    {
        var f = new Fixture();
        f.Runtime.RefuseSlot = EquipSlot.Body;
        Assert.False(Edit(f, "outfit").Success);
        Assert.Equal(99ul, f.Runtime.Slots[EquipSlot.Head].ItemId);
        Assert.Equal(20ul, f.Runtime.Slots[EquipSlot.Body].ItemId);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        f.Runtime.RefuseSlot = EquipSlot.Head;
        f.Runtime.Writes.Clear();
        Assert.False(step.Undo());
        Assert.Single(f.Runtime.Writes);
        f.Runtime.RefuseSlot = null;
        f.Runtime.Slots[EquipSlot.Body] = new(30, 0, 0); // unrelated later edit
        Assert.True(step.Undo());
        Assert.Equal(10ul, f.Runtime.Slots[EquipSlot.Head].ItemId);
        Assert.True(step.Redo());
        Assert.Equal(99ul, f.Runtime.Slots[EquipSlot.Head].ItemId);
        Assert.Equal(30ul, f.Runtime.Slots[EquipSlot.Body].ItemId);
    }

    [Fact]
    public void Equipment_history_does_not_write_to_a_replacement_generation()
    {
        var f = new Fixture();
        Assert.True(Edit(f, "item").Success);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        f.Runtime.Actor = f.Runtime.Actor with { Generation = f.Runtime.Actor.Generation + 1 };
        f.Runtime.Writes.Clear();
        Assert.True(step.Undo());
        Assert.True(step.Redo());
        Assert.Empty(f.Runtime.Writes);
    }

    [Fact]
    public void Body_change_captures_fresh_values_and_owns_its_redo_input()
    {
        var f = new Fixture();
        _ = f.Customize.Read(f.Runtime.Actor);
        f.Runtime.Customize[CustomizeKey.Gender] = 1;
        var desired = new Dictionary<CustomizeKey, int> { [CustomizeKey.Gender] = 0 };
        Assert.True(f.Customize.SetBody(f.Runtime.Actor, desired, "Swap gender").Success);
        desired[CustomizeKey.Gender] = 7;
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Replay(step, true);
        Assert.Equal(1, f.Runtime.Customize[CustomizeKey.Gender]);
        Replay(step, false);
        Assert.Equal(0, f.Runtime.Customize[CustomizeKey.Gender]);
    }

    [Fact]
    public void Body_change_undo_restores_values_normalized_by_the_provider()
    {
        var f = new Fixture();
        f.Runtime.NormalizeBody = true;
        f.Runtime.Customize[CustomizeKey.Gender] = 1;
        f.Runtime.Customize[CustomizeKey.BustSize] = 100;
        Assert.True(f.Customize.SetBody(f.Runtime.Actor,
            new Dictionary<CustomizeKey, int> { [CustomizeKey.Gender] = 0 }, "Swap gender").Success);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        Assert.Equal(0, f.Runtime.Customize[CustomizeKey.BustSize]);
        Replay(step, true);
        Assert.Equal(1, f.Runtime.Customize[CustomizeKey.Gender]);
        Assert.Equal(100, f.Runtime.Customize[CustomizeKey.BustSize]);
        Replay(step, false);
        Assert.Equal(0, f.Runtime.Customize[CustomizeKey.Gender]);
        Assert.Equal(0, f.Runtime.Customize[CustomizeKey.BustSize]);
    }

    [Fact]
    public void Revert_does_not_discard_a_look_that_cannot_be_captured()
    {
        var f = new Fixture();
        f.CaptureFailure = true;
        Assert.False(f.Wardrobe.Revert(f.Runtime.Actor).Success);
        Assert.False(f.History.CanUndo);
    }

    [Fact]
    public void Pending_appearance_capture_refuses_undo_without_mutation_then_can_retry()
    {
        var f = new Fixture();
        Assert.True(f.Customize.SetBody(f.Runtime.Actor,
            new Dictionary<CustomizeKey, int> { [CustomizeKey.Gender] = 1 }, "Swap gender").Success);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        f.CaptureFailure = true;
        Assert.False(step.Undo());
        Assert.Equal(1, f.Runtime.Customize[CustomizeKey.Gender]);
        Assert.True(step.RetainOnFailure);
        Assert.Equal("State unavailable", step.FailureDetail!());
        f.CaptureFailure = false;
        Replay(step, true);
        Assert.Equal(0, f.Runtime.Customize[CustomizeKey.Gender]);
        Replay(step, false);
        Assert.Equal(1, f.Runtime.Customize[CustomizeKey.Gender]);
    }

    private static void Replay(JournalStep step, bool undo)
    {
        Assert.True(undo ? step.Undo() : step.Redo());
        GestureResult? result = null;
        step.CompleteReplay!(undo, () => true, TestContext.Current.CancellationToken, r => result = r);
        Assert.True(result?.Success);
    }

    private static IntegrationResult Edit(Fixture f, string action) => action switch
    {
        "item" => f.Wardrobe.SetItem(f.Runtime.Actor, EquipSlot.Head, 99, 3, 4, "Item"),
        "dye" => f.Wardrobe.SetDye(f.Runtime.Actor, EquipSlot.Head, 1, 8, "Dye"),
        "facewear" => f.Wardrobe.SetFacewear(f.Runtime.Actor, 42, "Facewear"),
        "switch" => f.Wardrobe.SetSwitch(f.Runtime.Actor, MetaSwitch.HatVisible, false),
        _ => f.Wardrobe.SetOutfit(f.Runtime.Actor, "Outfit", _ => new(99, 3, 4)),
    };

    private static void EqualWardrobe(WardrobeState expected, WardrobeState actual)
    {
        Assert.Equal(expected.Facewear, actual.Facewear);
        Assert.Equal(expected.HatVisible, actual.HatVisible);
        Assert.Equal(expected.Slots, actual.Slots);
    }

    private sealed class Fixture : ISessionGenerationSource, IActorStateSnapshots
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
        public TransformHistory History { get; } = new();
        public RuntimeProxy Runtime { get; }
        public bool CaptureFailure;
        public IWardrobeControl Wardrobe { get; }
        public ICustomizeControl Customize { get; }

        public Fixture()
        {
            var port = DispatchProxy.Create<IIntegrationRuntimePort, RuntimeProxy>();
            Runtime = (RuntimeProxy)(object)port;
            var integration = new ActorIntegrationSession(port, null!, this);
            var disruptive = new DisruptiveSteps(History, this, new(), new ValueJournal(History));
            var journal = new ValueJournal(History);
            Wardrobe = new WardrobeSession(journal, integration, port, disruptive);
            Customize = new CustomizeSession(journal, integration, port, disruptive);
        }

        public IntegrationValue<ActorStateSnapshot> Capture(ActorId actor) =>
            CaptureFailure ? IntegrationValue<ActorStateSnapshot>.Fail("State unavailable") :
            IntegrationValue<ActorStateSnapshot>.Ok(new(actor, ActiveSessionGeneration!.Value,
                new(actor.LogicalId, new Dictionary<CustomizeKey, int>(Runtime.Customize), []),
                new(0, new(null, null, null, null, null), PresentationOverrides.None, null, null)));
        public void Restore(ActorStateSnapshot snapshot, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed)
        {
            if (!current() || cancellation.IsCancellationRequested) { completed(GestureResult.Fail("Cancelled")); return; }
            Runtime.Customize.Clear();
            foreach (var pair in (Dictionary<CustomizeKey, int>)snapshot.Pose.Pose)
                Runtime.Customize[pair.Key] = pair.Value;
            completed(GestureResult.Ok());
        }
        public void WaitForReset(ActorId actor, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed) => throw new NotSupportedException();
    }

    public class RuntimeProxy : DispatchProxy
    {
        public ActorId Actor = ActorId.New();
        public bool Refuse;
        public bool NormalizeBody;
        public EquipSlot? RefuseSlot;
        public List<string> Writes { get; } = new();
        public Dictionary<EquipSlot, WardrobeSlot> Slots { get; } = new()
        {
            [EquipSlot.Head] = new(10, 1, 2), [EquipSlot.Body] = new(20, 0, 0),
        };
        public Dictionary<CustomizeKey, int> Customize { get; } = new() { [CustomizeKey.Gender] = 0 };
        private ulong _facewear = 3;
        private bool _hatVisible = true;
        public WardrobeState ReadWardrobe() => new(new Dictionary<EquipSlot, WardrobeSlot>(Slots),
            _facewear, _hatVisible, false, true, true);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IIntegrationRuntimePort.IsResolvable): return (ActorId)args![0]! == Actor;
                case nameof(IIntegrationRuntimePort.ProbeGlamourerAccess): return GlamourerAccess.Editable;
                case nameof(IIntegrationRuntimePort.CaptureGlamourerState): return IntegrationValue<string>.Ok("baseline");
                case nameof(IIntegrationRuntimePort.GetActorName): return IntegrationValue<string>.Ok("Actor");
                case nameof(IIntegrationRuntimePort.GetWardrobeState): return IntegrationValue<WardrobeState>.Ok(ReadWardrobe());
                case nameof(IIntegrationRuntimePort.GetCustomizeState):
                    return IntegrationValue<CustomizeState>.Ok(new(new Dictionary<CustomizeKey, int>(Customize), 0));
                case nameof(IIntegrationRuntimePort.GetGlamourerStateJson):
                    return IntegrationValue<string>.Fail("State unavailable");
            }
            Writes.Add(method.Name);
            if (Refuse || method.Name == nameof(IIntegrationRuntimePort.SetItem) && (EquipSlot)args![1]! == RefuseSlot)
                return IntegrationPortResult.Fail("Write refused");
            switch (method.Name)
            {
                case nameof(IIntegrationRuntimePort.SetItem):
                    Slots[(EquipSlot)args![1]!] = new((ulong)args[2]!, (byte)args[3]!, (byte)args[4]!);
                    break;
                case nameof(IIntegrationRuntimePort.SetFacewear): _facewear = (ulong)args![1]!; break;
                case nameof(IIntegrationRuntimePort.SetMetaSwitch): _hatVisible = (bool)args![2]!; break;
                case nameof(IIntegrationRuntimePort.SetCustomize):
                    foreach (var (key, value) in (IReadOnlyDictionary<CustomizeKey, int>)args![1]!) Customize[key] = value;
                    if (NormalizeBody && Customize[CustomizeKey.Gender] == 0)
                        Customize[CustomizeKey.BustSize] = 0;
                    break;
                default: throw new NotSupportedException(method.Name);
            }
            return IntegrationPortResult.Ok();
        }
    }
}
