using System.Numerics;
using System.Reflection;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Tests.Journal;

public sealed class PresentationResetHistoryTests
{
    [Fact]
    public void Actor_page_reset_undo_redo_restores_colours_tint_wetness_and_original_captures()
    {
        var f = new Fixture(); f.Edit();
        var before = f.Session.OverridesFor(f.Actor);
        Assert.True(f.Values.ResetPresentation(f.Actor).Success);
        Assert.False(f.Session.OverridesFor(f.Actor).HasAny);
        // A new live reading cannot replace the original recovery baseline.
        f.Port.Colors[AppearanceColorChannel.Skin] = new Vector4(0.7f);
        Assert.True(f.Journal.Undo().Success);
        var restored = f.Session.OverridesFor(f.Actor);
        Assert.Equal(before.Colors.ToArray(), restored.Colors.ToArray());
        Assert.Equal(before.ColorCaptures.ToArray(), restored.ColorCaptures.ToArray());
        Assert.Equal(before.Tints.ToArray(), restored.Tints.ToArray());
        Assert.Equal(before.TintCaptures.ToArray(), restored.TintCaptures.ToArray());
        Assert.Equal(before.Wetness, restored.Wetness);
        Assert.Equal(before.WetnessCapture, restored.WetnessCapture);
        Assert.Equal(before.Colors[AppearanceColorChannel.Hair], f.Port.Colors[AppearanceColorChannel.Hair]);
        Assert.True(f.Journal.Redo().Success);
        Assert.False(f.Session.OverridesFor(f.Actor).HasAny);
        Assert.Equal(Vector4.One, f.Port.Colors[AppearanceColorChannel.Skin]);
        Assert.Equal(Vector4.One, f.Port.Tint);
        Assert.Equal(default, f.Port.Wetness);
    }

    [Fact]
    public void Refused_colour_replay_never_moves_history_and_preserves_captures_for_retry()
    {
        var f = new Fixture(); f.Edit();
        var capture = f.Session.OverridesFor(f.Actor).ColorCaptures;
        f.Values.ResetPresentation(f.Actor);
        var step = f.History.PeekUndo();
        f.Port.RefuseSet = true;
        Assert.False(f.Journal.Undo().Success);
        Assert.False(f.Journal.Undo().Success);
        Assert.Same(step, f.History.PeekUndo());
        Assert.False(f.History.CanRedo);
        Assert.Equal(capture.ToArray(), f.Session.OverridesFor(f.Actor).ColorCaptures.ToArray());
        f.Port.RefuseSet = false;
        Assert.True(f.Journal.Undo().Success);
        Assert.Same(step, f.History.PeekRedo());
        Assert.Equal(2, f.Session.OverridesFor(f.Actor).Colors.Count);
        f.Port.RefuseRestore = true;
        Assert.False(f.Journal.Redo().Success);
        Assert.Same(step, f.History.PeekRedo());
        Assert.Equal(capture.ToArray(), f.Session.OverridesFor(f.Actor).ColorCaptures.ToArray());
        f.Port.RefuseRestore = false;
        Assert.True(f.Journal.Redo().Success);
    }

    [Fact]
    public void Already_released_channel_remains_nullable_across_reset_history()
    {
        var f = new Fixture(); f.Edit();
        f.Session.BeginClearColor(f.Actor, AppearanceColorChannel.Skin,
            mutation => { mutation(); return PresentationPortResult.Ok(); }, _ => { });
        Assert.False(f.Session.OverridesFor(f.Actor).Colors.ContainsKey(AppearanceColorChannel.Skin));
        f.Values.ResetPresentation(f.Actor);
        Assert.True(f.Journal.Undo().Success);
        Assert.False(f.Session.OverridesFor(f.Actor).Colors.ContainsKey(AppearanceColorChannel.Skin));
        Assert.Equal(Vector4.One, f.Session.OverridesFor(f.Actor).ColorCaptures[AppearanceColorChannel.Skin]);
    }

    [Fact]
    public void Failed_initial_full_restore_retains_original_colour_captures_and_adds_no_history()
    {
        var f = new Fixture(); f.Edit();
        var before = f.Session.OverridesFor(f.Actor);
        f.Port.RefuseRestore = true;
        Assert.False(f.Values.ResetPresentation(f.Actor).Success);
        Assert.False(f.History.CanUndo);
        Assert.Equal(before.ColorCaptures.ToArray(), f.Session.OverridesFor(f.Actor).ColorCaptures.ToArray());
    }

    private sealed class Fixture
    {
        public readonly ActorId Actor = new(Guid.NewGuid(), 1);
        public readonly Port Port = new();
        public readonly ActorPresentationSession Session;
        public readonly ActorValueSession Values;
        public readonly TransformHistory History = new();
        public readonly UndoJournal Journal;
        public Fixture()
        {
            Session = new(Port);
            Values = new(new ValueJournal(History), Session, null!, null!, DispatchProxy.Create<IEntityBindings, BindingProxy>());
            var runner = new TransformGestureService(new SceneSession(new SelectionSession()),
                DispatchProxy.Create<ITransformRuntimePort, UnusedProxy>(), History);
            Journal = new(History, runner, new Keys(), new Lazy<IPoseSnapshotPort>(() => throw new Exception()), _ => true, _ => { });
        }
        public void Edit()
        {
            Session.SetColor(Actor, AppearanceColorChannel.Skin, Vector4.Zero);
            Session.SetColor(Actor, AppearanceColorChannel.Hair, new Vector4(0.4f));
            Session.SetTint(Actor, PresentationModel.Character, new Vector4(0.5f));
            Session.SetWetnessEnabled(Actor, true);
            Session.SetWetness(Actor, new(0.3f, 0.4f, 0.5f));
        }
    }

    public class BindingProxy : DispatchProxy
    {
        private readonly IActor _actor = DispatchProxy.Create<IActor, UnusedProxy>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            new BindingResult<IActor>(BindingStatus.Success, _actor);
    }
    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Unexpected runtime call");
    }
    private sealed class Keys : IActorStateKeySource { public ActorStateKey? Current(Guid lineage) => null; }

    private sealed class Port : IPresentationRuntimePort
    {
        public readonly Dictionary<AppearanceColorChannel, Vector4> Colors = Enum.GetValues<AppearanceColorChannel>()
            .ToDictionary(channel => channel, _ => Vector4.One);
        public Vector4 Tint = Vector4.One;
        public WetnessState Wetness;
        public bool RefuseSet, RefuseRestore;
        public bool IsSupported(ActorId actor) => true;
        public PresentationReading? Read(ActorId actor) => new(1, Tint, null, null, Wetness);
        public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor) =>
            IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Ok(new Dictionary<AppearanceColorChannel, Vector4>(Colors));
        public PresentationPortResult SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
        { if (RefuseSet) return PresentationPortResult.Fail("colour refused"); Colors[channel] = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult RestoreColors(ActorId actor, IReadOnlyDictionary<AppearanceColorChannel, Vector4> captures)
        { if (RefuseRestore) return PresentationPortResult.Fail("restore refused"); foreach (var (channel, value) in captures) Colors[channel] = value; return PresentationPortResult.Ok(); }
        public void BeginClearColor(ActorId actor, AppearanceColorChannel channel, Func<Action, PresentationPortResult> commit, Action<PresentationPortResult> completed) => completed(commit(() => { }));
        public PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 value) { Tint = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 value) { Tint = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult SetWetness(ActorId actor, WetnessState value) { Wetness = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult ClearWetness(ActorId actor, WetnessState value) { Wetness = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult SetOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult RestoreOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public void ClearOwned(ActorId actor) { }
    }
}
