using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Core;
using Poser.Game;
using Poser.History;
using Poser.Services;
using Poser.UI;

namespace Poser;

public class Poser : IDalamudPlugin
{
    public const string PluginName = "Poser";

    private readonly ServiceProvider _serviceProvider;

    public Poser(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop)
    {
        log.Info($"Starting {PluginName}...");

        // Build DI container
        _serviceProvider = ConfigureServices(
            pluginInterface,
            log,
            clientState,
            framework,
            objectTable,
            sigScanner,
            gameInterop);

        // Initialize UI Manager (triggers subscription to draw events)
        _ = _serviceProvider.GetRequiredService<IUIManager>();

        log.Info($"{PluginName} started successfully!");
    }

    private static ServiceProvider ConfigureServices(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop)
    {
        var services = new ServiceCollection();

        // Register Dalamud services
        services.AddSingleton(pluginInterface);
        services.AddSingleton(log);
        services.AddSingleton(clientState);
        services.AddSingleton(framework);
        services.AddSingleton(objectTable);
        services.AddSingleton(sigScanner);
        services.AddSingleton(gameInterop);

        // Register core services
        services.AddSingleton<EventBus>();

        // Register game services
        services.AddSingleton<IGPoseService, GPoseService>();
        services.AddSingleton<IActorManager, ActorManager>();
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<IAnimationService, AnimationService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IPosingService, PosingService>();

        // Register UI
        services.AddSingleton<IUIManager, UIManager>();

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
