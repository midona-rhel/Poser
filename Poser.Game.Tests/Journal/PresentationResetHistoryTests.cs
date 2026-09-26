using System.Numerics;
using System.Reflection;
using Poser.Application.Presentation;
using Poser.Application.Integration;
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
    public void Visibility_inverse_resolves_the_current_wrapper_instead_of_retaining_one()
    {
        var f = new Fixture();
        Assert.True(f.Values.SetVisibility(f.Actor, false).Success);
        var bindings = (BindingProxy)(object)f.Bindings;
        bindings.Actor = DispatchProxy.Create<IActor, UnusedProxy>();
        Assert.True(Assert.IsType<JournalStep>(f.History.PeekUndo()).Undo());
        Assert.Same(bindings.Actor, f.Visibility.LastActor);
        Assert.True(f.Visibility.Visible);
    }

    [Fact]
    public void Refused_tint_write_and_inverse_do_not_report_success()
    {
        var f = new Fixture();
        f.Port.RefuseSet = true;
        Assert.False(f.Values.SetTint(f.Actor, PresentationModel.Character, Vector4.Zero).Success);
        Assert.False(f.History.CanUndo);
        f.Port.RefuseSet = false;
        Assert.True(f.Values.SetTint(f.Actor, PresentationModel.Character, Vector4.Zero).Success);
        var step = Assert.IsType<JournalStep>(f.History.PeekUndo());
        f.Port.RefuseSet = true;
        Assert.False(step.Undo());
        Assert.True(step.RetainOnFailure);
        f.Port.RefuseSet = false;
        Assert.True(step.Undo());
        Assert.Equal(Vector4.One, f.Port.Tint);
    }

    [Fact]
    public void Visibility_refusal_reports_failure_and_adds_no_history()
    {
        var f = new Fixture();
        var spawn = f.Visibility;

        Assert.True(f.Values.SetVisibility(f.Actor, false).Success);
        var accepted = f.History.PeekUndo();
        spawn.Refuse = true;

        var refused = f.Values.SetVisibility(f.Actor, true);

        Assert.False(refused.Success);
        Assert.Contains("refused", refused.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(accepted, f.History.PeekUndo());
    }

    [Fact]
    public void First_custom_set_and_clear_have_synchronous_captured_value_inverses()
    {
        var f = new Fixture();
        Assert.True(f.ColorValues.Set(f.Actor, AppearanceColorChannel.Skin, Vector4.Zero).Success);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(Vector4.One, f.Port.Colors[AppearanceColorChannel.Skin]);
        Assert.Null(f.ColorValues.Override(f.Actor, AppearanceColorChannel.Skin));
        Assert.True(f.Journal.Redo().Success);
        Assert.Equal(Vector4.Zero, f.Port.Colors[AppearanceColorChannel.Skin]);
        f.Port.Colors[AppearanceColorChannel.Skin] = new Vector4(0.8f);
        Assert.True(f.ColorValues.Clear(f.Actor, AppearanceColorChannel.Skin).Success);
        Assert.Equal(Vector4.One, f.Port.Colors[AppearanceColorChannel.Skin]);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(Vector4.Zero, f.Port.Colors[AppearanceColorChannel.Skin]);
        Assert.True(f.Journal.Redo().Success);
        Assert.Equal(Vector4.One, f.Port.Colors[AppearanceColorChannel.Skin]);
        Assert.Null(f.ColorValues.Override(f.Actor, AppearanceColorChannel.Skin));
    }

    [Fact]
    public void Failed_clear_adds_no_step_and_failed_inverse_stays_retryable()
    {
        var f = new Fixture();
        Assert.True(f.ColorValues.Set(f.Actor, AppearanceColorChannel.Skin, Vector4.Zero).Success);
        var set = f.History.PeekUndo();
        f.Port.RefuseRestore = true;
        Assert.False(f.ColorValues.Clear(f.Actor, AppearanceColorChannel.Skin).Success);
        Assert.Same(set, f.History.PeekUndo());
        Assert.False(f.Journal.Undo().Success);
        Assert.False(f.Journal.Undo().Success);
        Assert.Same(set, f.History.PeekUndo());
        Assert.False(f.History.CanRedo);
        Assert.Equal(Vector4.Zero, f.ColorValues.Override(f.Actor, AppearanceColorChannel.Skin));
        f.Port.RefuseRestore = false;
        Assert.True(f.ColorValues.Clear(f.Actor, AppearanceColorChannel.Skin).Success);
        var clear = f.History.PeekUndo();
        f.Port.RefuseSet = true;
        Assert.False(f.Journal.Undo().Success);
        Assert.Same(clear, f.History.PeekUndo());
        f.Port.RefuseSet = false;
        Assert.True(f.Journal.Undo().Success);
        f.Port.RefuseRestore = true;
        Assert.False(f.Journal.Redo().Success);
        Assert.Same(clear, f.History.PeekRedo());
    }

    [Fact]
    public void Dead_generation_history_never_writes_to_replacement()
    {
        var f = new Fixture();
        f.ColorValues.Set(f.Actor, AppearanceColorChannel.Skin, Vector4.Zero);
        ((BindingProxy)(object)f.Bindings).Alive = false;
        f.Port.Colors[AppearanceColorChannel.Skin] = new Vector4(0.6f);
        Assert.True(f.Journal.Undo().Success);
        Assert.True(f.Journal.Redo().Success);
        Assert.Equal(new Vector4(0.6f), f.Port.Colors[AppearanceColorChannel.Skin]);
    }

    [Fact]
    public void Failed_first_custom_set_adds_no_history()
    {
        var f = new Fixture();
        f.Port.RefuseSet = true;
        Assert.False(f.ColorValues.Set(f.Actor, AppearanceColorChannel.Hair, Vector4.Zero).Success);
        Assert.False(f.History.CanUndo);
        Assert.Empty(f.Session.OverridesFor(f.Actor).ColorCaptures);
    }

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
        Assert.True(f.Session.ClearColor(f.Actor, AppearanceColorChannel.Skin).Success);
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
        public readonly AppearanceColorSession ColorValues;
        public readonly IEntityBindings Bindings = DispatchProxy.Create<IEntityBindings, BindingProxy>();
        public readonly IActorSpawnService Spawn =
            DispatchProxy.Create<IActorSpawnService, VisibilitySpawnProxy>();
        public readonly TransformHistory History = new();
        public readonly UndoJournal Journal;
        public Fixture()
        {
            ((BindingProxy)(object)Bindings).ActorId = Actor;
            Session = new(Port);
            var values = new ValueJournal(History);
            Values = new(values, Session, new Game.Presentation.ActorValueRuntime(Bindings, Spawn));
            var runner = new TransformGestureService(new SceneSession(new SelectionSession()),
                DispatchProxy.Create<ITransformRuntimePort, UnusedProxy>(), History);
            Journal = new(History, runner, _ => true, _ => { });
            var integration = new ActorIntegrationSession(DispatchProxy.Create<IIntegrationRuntimePort, IntegrationProxy>(), null!, null!);
            ColorValues = new(Session, integration, values, runner, Bindings);
        }
        public VisibilitySpawnProxy Visibility =>
            (VisibilitySpawnProxy)(object)Spawn;
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
        public bool Alive = true;
        public ActorId ActorId;
        public IActor Actor { get; set; } = DispatchProxy.Create<IActor, UnusedProxy>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "GetActorId" => Alive ? ActorId : null,
                "Resolve" => Alive
                    ? new BindingResult<IActor>(BindingStatus.Success, Actor)
                    : new BindingResult<IActor>(BindingStatus.Missing),
                _ => throw new InvalidOperationException(
                    "Unexpected binding call: " + targetMethod?.Name),
            };
    }
    public class VisibilitySpawnProxy : DispatchProxy
    {
        public bool Visible = true;
        public bool Refuse;
        public IActor? LastActor;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "IsVisible" => Visible,
                "SetVisibility" => Set((IActor)args![0]!, (bool)args[1]!),
                _ => throw new InvalidOperationException(
                    "Unexpected spawn call: " + targetMethod?.Name),
            };

        private object? Set(IActor actor, bool visible)
        {
            LastActor = actor;
            if (!Refuse)
                Visible = visible;
            return null;
        }
    }
    public class IntegrationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "ProbeGlamourerAccess" => GlamourerAccess.Editable,
            "CaptureGlamourerState" => IntegrationValue<string>.Ok("{}"),
            "GetActorName" => IntegrationValue<string>.Ok("Actor"),
            _ => throw new InvalidOperationException("Unexpected integration call: " + targetMethod?.Name),
        };
    }
    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Unexpected runtime call");
    }

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
        public PresentationPortResult RestoreColor(ActorId actor, AppearanceColorChannel channel, Vector4 incoming)
        { if (RefuseRestore) return PresentationPortResult.Fail("restore refused"); Colors[channel] = incoming; return PresentationPortResult.Ok(); }
        public PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 value) { if (RefuseSet) return PresentationPortResult.Fail("tint refused"); Tint = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 value) { Tint = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult SetWetness(ActorId actor, WetnessState value) { Wetness = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult ClearWetness(ActorId actor, WetnessState value) { Wetness = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult SetOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult RestoreOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public void ClearOwned(ActorId actor) { }
    }
}
