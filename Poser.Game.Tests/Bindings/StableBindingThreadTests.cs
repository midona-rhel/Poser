using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Domain.Identity;
using Poser.Game.Bindings;
using Poser.Game.Services;

namespace Poser.Game.Tests.Bindings;

public sealed class StableBindingThreadTests
{
    [Theory]
    [InlineData("actor")]
    [InlineData("light")]
    public void Off_thread_resolve_reports_wrong_thread(string kind)
    {
        var framework = DispatchProxy.Create<IFramework, OffThread>();
        // Nothing past the thread check may be touched: every other
        // dependency is null.
        var registry = new StableBindingRegistry(
            null!, null!, null!, null!, null!, null!, null!, null!, framework);

        var (status, detail) = kind == "actor"
            ? Read(registry.Resolve(ActorId.New()))
            : Read(registry.Resolve(LightId.New()));

        Assert.Equal(BindingStatus.WrongThread, status);
        Assert.Contains("framework thread", detail);
    }

    private static (BindingStatus, string?) Read<T>(BindingResult<T> result) where T : class =>
        (result.Status, result.Detail);

    public class OffThread : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == "get_IsInFrameworkUpdateThread"
                ? false
                : throw new InvalidOperationException(method.Name);
    }
}
