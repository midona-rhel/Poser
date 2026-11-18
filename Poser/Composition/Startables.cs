using System;
using System.Linq;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Poser.Composition;

/// <summary>The services Poser starts before its window first draws, in the
/// order listed here. Each feature adds its own entries. A service that only
/// starts listening for events when it is created, and is not listed here,
/// does nothing until other code asks for it.</summary>
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
    RunningActions,
}

internal sealed record Startable(StartStage Stage, Action<IServiceProvider> Start);

internal static class Startables
{
    /// <summary>Adds a service that starts simply by being created.</summary>
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
