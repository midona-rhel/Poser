using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Files;
using Poser.Library;
using Poser.Scene;

namespace Poser.Game.Scene;

public static class SceneWorkflowRegistration
{
    public static IServiceCollection AddSceneWorkflow(this IServiceCollection services)
    {
        services.AddSingleton<ISceneDocumentStore, SceneDocumentStore>();
        services.AddSingleton<ISceneRuntime, SceneRuntimeAdapter>();
        // Resolve the runtime before the workflow: container disposal drains the
        // workflow first, then tears down the runtime's subscriptions and files.
        services.AddSingleton<SceneWorkflow>(sp => new SceneWorkflow(
            sp.GetRequiredService<ISceneRuntime>(),
            sp.GetRequiredService<ISceneDocumentStore>(),
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<SceneGroups>(),
            sp.GetRequiredService<IPoseLibraryService>(),
            sp.GetRequiredService<GroupTransformState>(),
            sp.GetRequiredService<TransformHistory>()));
        return services;
    }
}
