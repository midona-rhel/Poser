using System;
using System.Linq;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Poser.Composition;

/// <summary>Everything the host starts before the UI draws, in start order:
/// the declaration order IS the order. Each feature module registers its own
/// entries; a service that subscribes on construction and is not listed here
/// stays inert until something resolves it.</summary>
internal enum StartStage
{
    AutoSave,
    PropSpawns,
    OverlayNodes,
    WorldObjects,
    Lighting,
    Cameras,
    Environment,
    Bindings,
    Animation,
    DebugBridge,
    Gaze,
    Integration,
    AppearanceCatalogWarmup,
    WorldRendering,
    SceneWorkflow,
    SceneCreation,
    CameraWorkspace,
    ParentingFrames,
    SceneAutoSave,
    SceneLifecycle,
    TargetSync,
    MouseTarget,
    CharacterFiles,
}

internal sealed record Startable(StartStage Stage, Action<IServiceProvider> Start);

internal static class Startables
{
    /// <summary>Starts a service whose constructor is its start: resolving
    /// it subscribes it.</summary>
    public static IServiceCollection AddStartable<T>(
        this IServiceCollection services, StartStage stage) where T : notnull =>
        services.AddStartable(stage, sp => sp.GetRequiredService<T>());

    public static IServiceCollection AddStartable(
        this IServiceCollection services, StartStage stage, Action<IServiceProvider> start) =>
        services.AddSingleton(new Startable(stage, start));

    public static void StartAll(IServiceProvider services, IPluginLog log)
    {
        foreach (var startable in services.GetServices<Startable>().OrderBy(s => s.Stage))
        {
            log.Debug($"Load link: {startable.Stage}");
            startable.Start(services);
        }
    }
}
