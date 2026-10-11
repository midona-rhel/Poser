using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Scene;
using Poser.Game.Services;

namespace Poser.Game.Tests.Scene;

public sealed class SceneObjectControlTests
{
    [Fact]
    public void Model_edit_uses_existing_history_and_readings_are_detached()
    {
        var id = new PropId(Guid.NewGuid(), 0);
        var model = new PropModel("Test", 1, 1, 1, "");
        var original = model;
        var prop = Stub<IPropHandle>((method, args) => method switch
        {
            "get_IsValid" or "get_Visible" => true,
            "get_Name" => "Test",
            "get_Model" => model,
            "Respawn" => Replace(args!),
            _ => throw new InvalidOperationException(method),
        });
        bool Replace(object?[] args)
        {
            model = (PropModel)args[0]!;
            args[1] = null;
            return true;
        }
        var bindings = Stub<IEntityBindings>((method, args) =>
        {
            Assert.Equal("Resolve", method);
            Assert.Equal(id, args![0]);
            return new BindingResult<IPropHandle>(BindingStatus.Success, prop);
        });
        var history = new EditHistory();
        var journal = new ValueJournal(history);
        var control = new SceneObjectControl(bindings, journal);
        var reading = control.Read(id)!;
        var edited = model with { AnimationVariant = 3, Stain0 = 12 };
        Assert.True(control.SetModel(id, edited).Success);
        Assert.Equal(edited, model);
        Assert.Equal(original, reading.Model);
        var entry = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(entry.Undo());
        Assert.Equal(original, model);
        Assert.True(entry.Redo());
        Assert.Equal(edited, model);
    }

    [Fact]
    public void Transport_write_lands_without_a_step_and_a_stale_write_fails()
    {
        var id = WorldObjectId.New();
        bool paused = false, current = true;
        var effect = Stub<IWorldObject>((method, args) => method switch
        {
            "get_IsValid" => true,
            "get_VfxPaused" => paused,
            "set_VfxPaused" => paused = (bool)args![0]!,
            _ => throw new InvalidOperationException(method),
        });
        var bindings = Stub<IEntityBindings>((method, args) => current
            ? new BindingResult<IWorldObject>(BindingStatus.Success, effect)
            : new BindingResult<IWorldObject>(BindingStatus.StaleTarget));
        var history = new EditHistory();
        var control = new SceneObjectControl(bindings, new ValueJournal(history));

        Assert.True(control.Set(id, WorldObjectProperties.VfxPaused, true).Success);
        Assert.True(paused);
        Assert.False(history.CanUndo);

        current = false;
        var stale = control.Set(id, WorldObjectProperties.VfxPaused, false);
        Assert.Equal((false, "The object is no longer available."), (stale.Success, stale.Detail));
        Assert.True(paused);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task World_object_model_replacement_is_one_undoable_step()
    {
        // #493: a replaced furniture model added no step, so Undo reversed
        // an earlier, unrelated action instead.
        var id = WorldObjectId.New();
        string path = "bgcommon/old.sgb";
        bool refuse = false;
        var world = Stub<IWorldObject>((method, args) => method switch
        {
            "get_IsValid" => true,
            "get_Path" => path,
            "Respawn" => Respawn((string)args![0]!),
            _ => throw new InvalidOperationException(method),
        });
        Task<Poser.Domain.Outcome> Respawn(string next)
        {
            if (refuse) return Task.FromResult(new Poser.Domain.Outcome(false, "The game did not take it."));
            path = next;
            return Task.FromResult(Poser.Domain.Outcome.Ok());
        }
        var bindings = Stub<IEntityBindings>((_, _) => new BindingResult<IWorldObject>(BindingStatus.Success, world));
        var history = new EditHistory();
        var control = new SceneObjectControl(bindings, new ValueJournal(history), history: history);

        Assert.True((await control.Respawn(id, "bgcommon/new.sgb")).Success);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Poser.Domain.Transforms.GestureResult? done = null;
        step.CompleteReplay!(true, () => true, CancellationToken.None, result => done = result);
        Assert.True(done?.Success);
        Assert.Equal("bgcommon/old.sgb", path);
        step.CompleteReplay!(false, () => true, CancellationToken.None, result => done = result);
        Assert.Equal("bgcommon/new.sgb", path);

        // A refused or same-path replacement records nothing.
        refuse = true;
        Assert.False((await control.Respawn(id, "bgcommon/other.sgb")).Success);
        refuse = false;
        Assert.True((await control.Respawn(id, "bgcommon/new.sgb")).Success);
        Assert.Same(step, history.PeekUndo());
    }

    [Fact]
    public async Task Model_replay_forwards_validity_and_completes_when_respawn_faults()
    {
        string path = "old";
        bool replay = false;
        var pending = new TaskCompletionSource<Poser.Domain.Outcome>();
        Func<bool>? forwarded = null;
        CancellationToken forwardedToken = default;
        var world = Stub<IWorldObject>((method, args) => method switch
        {
            "get_IsValid" => true,
            "get_Path" => path,
            "Respawn" => Respawn(args!),
            _ => throw new InvalidOperationException(method),
        });
        Task<Poser.Domain.Outcome> Respawn(object?[] args)
        {
            if (!replay)
            {
                path = (string)args[0]!;
                return Task.FromResult(Poser.Domain.Outcome.Ok());
            }
            forwarded = (Func<bool>?)args[1];
            forwardedToken = (CancellationToken)args[2]!;
            return pending.Task;
        }
        var bindings = Stub<IEntityBindings>((_, _) => new BindingResult<IWorldObject>(BindingStatus.Success, world));
        var history = new EditHistory();
        var control = new SceneObjectControl(bindings, new ValueJournal(history), history: history);
        await control.Respawn(WorldObjectId.New(), "new");
        replay = true;
        using var cancellation = new CancellationTokenSource();
        Func<bool> current = () => !cancellation.IsCancellationRequested;
        var completed = new TaskCompletionSource<Poser.Domain.Transforms.GestureResult>();
        Assert.IsType<JournalStep>(history.PeekUndo()).CompleteReplay!(true, current, cancellation.Token,
            result => completed.SetResult(result));
        Assert.Same(current, forwarded);
        Assert.Equal(cancellation.Token, forwardedToken);
        cancellation.Cancel();
        Assert.False(forwarded!());
        pending.SetException(new InvalidOperationException("test failure"));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Equal("test failure", result.Detail);
    }

    private static T Stub<T>(Func<string, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Call = call;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args);
    }
}
