using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<ISceneRuntime, SceneRuntimeAdapter>();
        services.AddSingleton<ISceneStructure, SceneStructure>();
        services.AddSingleton<ISceneWorkflowObserver, SceneWorkflowObserver>();
        // Resolve the runtime before the workflow: container disposal drains the
        // workflow first, then tears down the runtime's subscriptions and files.
        services.AddSingleton<SceneWorkflow>(sp => new SceneWorkflow(
            sp.GetRequiredService<ISceneRuntime>(),
            sp.GetRequiredService<ISceneDocumentStore>(),
            sp.GetRequiredService<ISceneWorkflowObserver>(),
            sp.GetRequiredService<TransformHistory>(),
            sp.GetRequiredService<ISceneStructure>()));
        return services;
    }
}
