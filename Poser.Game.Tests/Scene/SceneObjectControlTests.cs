using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Scene;
using Poser.Services;

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
        var history = new TransformHistory();
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
        var history = new TransformHistory();
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
