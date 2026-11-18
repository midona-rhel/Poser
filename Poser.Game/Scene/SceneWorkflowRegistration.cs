using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Documents.Scene;

namespace Poser.Game.Scene;

public static class SceneWorkflowRegistration
{
    public static IServiceCollection AddSceneWorkflow(this IServiceCollection services)
    {
        services.AddSingleton<ISceneDocumentStore, SceneDocumentStore>();
        // All the scene services below share one table of handles, so a
        // handle one of them gives out can be looked up by the others.
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
        // The workflow is created after the services it uses. Services are
        // disposed in reverse order, so when the plugin closes the workflow
        // finishes its work first, and only then do those services remove
        // their event listeners and files.
        services.AddSingleton<SceneWorkflow>(sp => new SceneWorkflow(
            sp.GetRequiredService<ISceneStatePort>(),
            sp.GetRequiredService<ISceneCapturePort>(),
            sp.GetRequiredService<ISceneMaterializer>(),
            sp.GetRequiredService<IActorRestorePort>(),
            sp.GetRequiredService<ISceneHistoryPort>(),
            sp.GetRequiredService<ISceneDocumentStore>(),
            sp.GetRequiredService<ISceneWorkflowObserver>(),
            sp.GetRequiredService<EditHistory>(),
            sp.GetRequiredService<ISceneStructure>(),
            sp.GetRequiredService<TransformParenting>(),
            () => sp.GetRequiredService<Poser.Application.Settings.ConfigurationService>().Config.Library.InstallId));
        return services;
    }
}
