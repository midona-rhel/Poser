using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Game.Transforms;
using Poser.Documents.Scene;

namespace Poser.Game.Scene;

/// <summary>Registers scene saving and loading and the classes it uses.</summary>
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
        services.AddSingleton<SceneClearer>();
        services.AddSingleton<GPoseSceneState>();
        services.AddSingleton<SceneAppearance>();
        services.AddSingleton<SceneActorRestore>();
        services.AddSingleton<SceneEntityLoader>();
        services.AddSingleton<ISceneStructure, SceneStructure>();
        services.AddSingleton<SceneWorkflowObserver>();
        // The workflow is created after the services it uses. Services are
        // disposed in reverse order, so when the plugin closes the workflow
        // finishes its work first, and only then do those services remove
        // their event listeners and files.
        services.AddSingleton<SceneWorkflow>(sp => new SceneWorkflow(
            sp.GetRequiredService<GPoseSceneState>(),
            sp.GetRequiredService<SceneAppearance>(),
            sp.GetRequiredService<SceneEntityLoader>(),
            sp.GetRequiredService<SceneActorRestore>(),
            sp.GetRequiredService<SceneHistoryBinder>(),
            sp.GetRequiredService<ISceneDocumentStore>(),
            sp.GetRequiredService<SceneWorkflowObserver>(),
            sp.GetRequiredService<EditHistory>(),
            sp.GetRequiredService<ISceneStructure>(),
            sp.GetRequiredService<TransformParenting>(),
            () => sp.GetRequiredService<Poser.Application.Settings.ConfigurationService>().Config.Library.InstallId));
        return services;
    }
}
