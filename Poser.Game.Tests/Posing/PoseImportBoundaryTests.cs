using System.Reflection;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Posing;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Posing;

public sealed class PoseImportBoundaryTests
{
    [Fact]
    public void UnavailableTargetsRefuseBeforeNativePlanning()
    {
        var id = new ActorId(Guid.NewGuid(), 3);
        var bindings = Stub<IEntityBindings>((method, args) =>
        {
            Assert.Equal("Resolve", method);
            Assert.Equal(id, Assert.IsType<ActorId>(args![0]));
            return new BindingResult<IActor>(BindingStatus.StaleTarget);
        });
        // Missing dependencies deliberately fail if any refused route reaches native planning.
        IPoseImportCommands imports = new NativePoseImportService(
            bindings, null!, null!, null!, null!);
        Assert.False(imports.HasPosableSkeleton(id));
        Assert.Null(imports.InspectPose(id, new PoseFile()));
        var results = new[]
        {
            imports.ImportPose(id, "not-read.pose", new()),
            imports.ImportPose(id, new PoseFile(), new(), "Import"),
            imports.ApplyRestPose(id, RestPose.APose),
            imports.ApplyReferencePose(id),
        };
        foreach (var result in results)
            Assert.False(result.Success);
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
