using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Bindings;
using Poser.Game.Core;

namespace Poser.Game.Integration;

/// <summary>
/// Resolves an exact stable actor generation to its object index at the
/// call boundary for the three IPC clients; nothing native is retained.
/// Address-addressed spawn calls get their framework-thread and non-null
/// preconditions here too.
/// </summary>
public sealed class IntegrationActorResolution : IIntegrationResolutionPort
{
    private readonly IFramework _framework;
    // Resolve lazily to break the constructor dependency cycle through
    // StableBindingRegistry, actor spawning, and PenumbraIpc.
    private readonly Lazy<StableBindingRegistry> _bindings;
    private readonly IObjectTable _objects;

    public IntegrationActorResolution(
        IFramework framework,
        Lazy<StableBindingRegistry> bindings,
        IObjectTable objects)
    {
        _framework = framework;
        _bindings = bindings;
        _objects = objects;
    }

    public Task<T> OnFrameworkThread<T>(Func<T> action) =>
        _framework.RunOnFrameworkThread(action);

    public bool IsResolvable(ActorId actor)
    {
        var resolved = _bindings.Value.Resolve(actor);
        return resolved.Success && resolved.Value is { } legacy && legacy.Address != nint.Zero;
    }

    public IntegrationValue<string> GetActorName(ActorId actor)
    {
        int index = ResolveIndex(actor, out var detail);
        if (index < 0)
            return IntegrationValue<string>.Fail(detail!);
        string name = _objects[index]?.Name.TextValue ?? string.Empty;
        return name.Length == 0
            ? IntegrationValue<string>.Fail("The actor has no readable name.")
            : IntegrationValue<string>.Ok(name);
    }

    internal bool OnFrameworkThreadNow => _framework.IsInFrameworkUpdateThread;

    internal int ResolveIndex(ActorId actor, out string? detail)
    {
        detail = null;
        var resolved = _bindings.Value.Resolve(actor);
        if (!resolved.Success || resolved.Value is not { } legacy || legacy.Address == nint.Zero)
        {
            detail = resolved.Detail ?? "The actor is no longer available.";
            return -1;
        }
        return GPoseObjectTable.IndexOf(legacy.Address);
    }

    /// <summary>The resolved address; only after <see cref="ResolveIndex"/> succeeded.</summary>
    internal nint AddressOf(ActorId actor) => _bindings.Value.Resolve(actor).Value!.Address;

    /// <summary>Checks the framework thread and nonzero addresses for spawn calls
    /// made before stable bindings exist. It does not validate address ownership.</summary>
    internal IntegrationResult? AddressPair(nint first, nint second)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return IntegrationResult.Fail(
                "External integration calls must run on the framework thread.");
        return first == nint.Zero || second == nint.Zero
            ? IntegrationResult.Fail("The clone or its source has no address.")
            : null;
    }
}
