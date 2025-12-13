using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Config;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Game;
using Poser.History;
using Poser.Services;
using Poser.UI;

namespace Poser;

public class Poser : IDalamudPlugin
{
    public const string PluginName = "Poser";
    private const string CommandName = "/poser";

    private readonly ServiceProvider _serviceProvider;
    private readonly ICommandManager _commandManager;

    public Poser(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        ICommandManager commandManager,
        IDataManager dataManager,
        IKeyState keyState,
        ITextureProvider textureProvider)
    {
        log.Info($"Starting {PluginName}...");

        _commandManager = commandManager;

        // Initialize bone info service with logger
        BoneInfoService.Initialize(log);

        // Build DI container
        _serviceProvider = ConfigureServices(
            pluginInterface,
            log,
            clientState,
            framework,
            objectTable,
            sigScanner,
            gameInterop,
            commandManager,
            dataManager,
            keyState,
            textureProvider);

        // Initialize configuration service (sets static Instance, must be before UI)
        _ = _serviceProvider.GetRequiredService<ConfigurationService>();

        // Initialize UI Manager (triggers subscription to draw events)
        _ = _serviceProvider.GetRequiredService<IUIManager>();

        // Register the /poser command
        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Poser window"
        });

        log.Info($"{PluginName} started successfully!");
    }

    private void OnCommand(string command, string args)
    {
        var uiManager = _serviceProvider.GetRequiredService<IUIManager>();
        uiManager.ToggleMainWindow();
    }

    private static ServiceProvider ConfigureServices(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IClientState clientState,
        IFramework framework,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        ICommandManager commandManager,
        IDataManager dataManager,
        IKeyState keyState,
        ITextureProvider textureProvider)
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
        services.AddSingleton(commandManager);
        services.AddSingleton(dataManager);
        services.AddSingleton(keyState);
        services.AddSingleton(textureProvider);

        // Register configuration service (must be early - others depend on it)
        services.AddSingleton<ConfigurationService>();

        // Register core services
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());

        // Register game services
        services.AddSingleton<IGPoseService, GPoseService>();
        services.AddSingleton<IActorManager, ActorManager>();
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<IAnimationService, AnimationService>();
        services.AddSingleton<IAnimationDataService, AnimationDataService>();
        services.AddSingleton<IActorSpawnService, ActorSpawnService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IPosingService, PosingService>();
        services.AddSingleton<IGazeService, GazeService>();
        services.AddSingleton<ISkeletonService, SkeletonService>();
        services.AddSingleton<IIKService, IKService>();
        services.AddSingleton<IBonePosingService, BonePosingService>();
        services.AddSingleton<ISelectionService, SelectionService>();
        services.AddSingleton<IEditorState, EditorState>();

        // Register UI
        services.AddSingleton<IUIManager, UIManager>();

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(CommandName);
        _serviceProvider.Dispose();
    }
}
