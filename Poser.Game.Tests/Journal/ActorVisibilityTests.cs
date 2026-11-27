using System.Reflection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Presentation;
using Poser.Game.Services;

namespace Poser.Game.Tests.Journal;

/// <summary>Showing and hiding an actor through ActorValueSession, and undo of it.</summary>
public sealed class ActorVisibilityTests
{
    [Fact]
    public void Visibility_inverse_resolves_the_current_wrapper_instead_of_retaining_one()
    {
        var f = new Fixture();
        Assert.True(f.Values.SetVisibility(f.Actor, false).Success);
        f.Bindings.Actor = DispatchProxy.Create<IActor, UnusedProxy>();
        Assert.True(Assert.IsType<JournalStep>(f.History.PeekUndo()).Undo());
        Assert.Same(f.Bindings.Actor, f.Spawn.LastActor);
        Assert.True(f.Spawn.Visible);
    }

    [Fact]
    public void Visibility_refusal_reports_failure_and_adds_no_history()
    {
        var f = new Fixture();
        Assert.True(f.Values.SetVisibility(f.Actor, false).Success);
        var accepted = f.History.PeekUndo();
        f.Spawn.Refuse = true;

        var refused = f.Values.SetVisibility(f.Actor, true);

        Assert.False(refused.Success);
        Assert.Contains("refused", refused.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(accepted, f.History.PeekUndo());
    }

    private sealed class Fixture
    {
        public readonly ActorId Actor = new(Guid.NewGuid(), 1);
        public readonly EditHistory History = new();
        public readonly BindingProxy Bindings;
        public readonly VisibilitySpawnProxy Spawn;
        public readonly ActorValueSession Values;

        public Fixture()
        {
            var bindings = DispatchProxy.Create<IEntityBindings, BindingProxy>();
            var spawn = DispatchProxy.Create<IActorSpawnService, VisibilitySpawnProxy>();
            Bindings = (BindingProxy)(object)bindings;
            Spawn = (VisibilitySpawnProxy)(object)spawn;
            // Visibility never reaches the presentation session.
            Values = new(new ValueJournal(History), null!, bindings, spawn);
        }
    }

    public class BindingProxy : DispatchProxy
    {
        public IActor Actor { get; set; } = DispatchProxy.Create<IActor, UnusedProxy>();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "Resolve"
                ? new BindingResult<IActor>(BindingStatus.Success, Actor)
                : throw new InvalidOperationException("Unexpected binding call: " + targetMethod?.Name);
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
                _ => throw new InvalidOperationException("Unexpected spawn call: " + targetMethod?.Name),
            };

        private object? Set(IActor actor, bool visible)
        {
            LastActor = actor;
            if (!Refuse)
                Visible = visible;
            return null;
        }
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Unexpected runtime call");
    }
}
