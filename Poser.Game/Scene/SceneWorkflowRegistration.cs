using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Files;
using Poser.Scene;

namespace Poser.Game.Scene;

public static class SceneWorkflowRegistration
{
    public static IServiceCollection AddSceneWorkflow(this IServiceCollection services)
    {
        services.AddSingleton<ISceneDocumentStore, SceneDocumentStore>();
        // One receipt table for every scene port: a handle one port issues is
        // resolved by the others.
        services.AddSingleton(sp =>
        {
            var sessions = sp.GetRequiredService<ISessionGenerationSource>();
            return new SceneRuntimeHandles(() => sessions.ActiveSessionGeneration);
        });
        services.AddSingleton<SceneHistoryBinder>();
        services.AddSingleton<SceneClearRuntime>();
        services.AddSingleton<SceneStateRuntime>();
        services.AddSingleton<SceneAppearanceRuntime>();
        services.AddSingleton<SceneActorRuntime>();
        services.AddSingleton<SceneEntityRuntime>();
        services.AddSingleton<ISceneStatePort>(sp => sp.GetRequiredService<SceneStateRuntime>());
        services.AddSingleton<ISceneCapturePort>(sp => sp.GetRequiredService<SceneAppearanceRuntime>());
        services.AddSingleton<ISceneMaterializer>(sp => sp.GetRequiredService<SceneEntityRuntime>());
        services.AddSingleton<IActorRestorePort>(sp => sp.GetRequiredService<SceneActorRuntime>());
        services.AddSingleton<ISceneHistoryPort>(sp => sp.GetRequiredService<SceneHistoryBinder>());
        services.AddSingleton<ISceneStructure, SceneStructure>();
        services.AddSingleton<ISceneWorkflowObserver, SceneWorkflowObserver>();
        // Resolve the runtime ports before the workflow: container disposal
        // drains the workflow first, then tears down the ports' subscriptions
        // and files.
        services.AddSingleton<SceneWorkflow>(sp => new SceneWorkflow(
            sp.GetRequiredService<ISceneStatePort>(),
            sp.GetRequiredService<ISceneCapturePort>(),
            sp.GetRequiredService<ISceneMaterializer>(),
            sp.GetRequiredService<IActorRestorePort>(),
            sp.GetRequiredService<ISceneHistoryPort>(),
            sp.GetRequiredService<ISceneDocumentStore>(),
            sp.GetRequiredService<ISceneWorkflowObserver>(),
            sp.GetRequiredService<TransformHistory>(),
            sp.GetRequiredService<ISceneStructure>(),
            sp.GetRequiredService<TransformParenting>()));
        return services;
    }
}
