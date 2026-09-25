using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Journal;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Tests.Scene;

public sealed class SceneObjectControlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stale_or_off_thread_edits_never_reach_native_objects(bool frameworkThread)
    {
        var prop = new PropId(Guid.NewGuid(), 2);
        var world = new WorldObjectId(Guid.NewGuid(), 2);
        var bindings = Stub<IEntityBindings>((method, args) =>
        {
            Assert.True(frameworkThread); // Off-thread requests must not even resolve.
            Assert.Equal("Resolve", method);
            return args![0] switch
            {
                PropId id when id == prop => new BindingResult<IPropHandle>(BindingStatus.StaleTarget),
                WorldObjectId id when id == world => new BindingResult<IWorldObject>(BindingStatus.StaleTarget),
                _ => throw new InvalidOperationException("A request changed its target."),
            };
        });
        var framework = Stub<IFramework>((_, _) => frameworkThread);
        ISceneObjectControl control = new SceneObjectControl(bindings, framework, null!, null!);
        Assert.Null(control.Read(prop));
        Assert.Null(control.Read(world));
        Assert.False(control.SetVisible(prop, false).Success);
        Assert.False(control.SetModel(prop, default).Success);
        Assert.False(control.SetOpacity(world, 0.5f).Success);
        Assert.False((await control.Respawn(world, "unused.mdl")).Success);
        Assert.Null(control.ReadDebug(world));
        Assert.False(control.ToggleObjectFlag(world, 1).Success);
        Assert.False(control.ToggleDrawFlag(world, 1).Success);
    }

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
        var control = new SceneObjectControl(bindings, Stub<IFramework>((_, _) => true),
            new PropSession(journal), new WorldObjectSession(journal));
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
