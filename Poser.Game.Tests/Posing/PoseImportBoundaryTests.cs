using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Tests.Posing;

public sealed class PoseImportBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnavailableTargetsRefuseBeforeNativePlanning(bool frameworkThread)
    {
        var id = new ActorId(Guid.NewGuid(), 3);
        int resolutions = 0;
        var bindings = Stub<IEntityBindings>((method, args) =>
        {
            Assert.Equal("Resolve", method);
            Assert.Equal(id, Assert.IsType<ActorId>(args![0]));
            resolutions++;
            return new BindingResult<IActor>(BindingStatus.StaleTarget, Detail: "Stale actor.");
        });
        var framework = Stub<IFramework>((method, _) =>
        {
            Assert.Equal("get_IsInFrameworkUpdateThread", method);
            return frameworkThread;
        });
        // Missing dependencies deliberately fail if any refused route reaches native planning.
        IPoseImportCommands imports = new NativePoseImportService(
            bindings, null!, null!, null!, null!, framework);
        Assert.False(imports.HasPosableSkeleton(id));
        var results = new[]
        {
            imports.ImportPose(id, "not-read.pose", new()),
            imports.ImportPose(id, new PoseFile(), new(), "Import"),
            imports.ApplyRestPose(id, RestPose.APose),
            imports.ApplyReferencePose(id),
        };
        foreach (var result in results)
        {
            Assert.False(result.Success);
            Assert.Equal(frameworkThread ? "Stale actor." :
                "Pose import must run on the framework thread.", result.Detail);
        }
        if (!frameworkThread)
            Assert.Equal(0, resolutions);
    }

    private static T Stub<T>(Func<string, object?[]?, object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)result).Call = invoke;
        return result;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Call(targetMethod!.Name, args);
    }
}
