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
using Poser.Files;
using Poser.Game;
using Poser.History;
using Poser.IPC;
using Poser.Services;
using Poser.UI;

namespace Poser;

public class Poser : IDalamudPlugin
{
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
        ITextureProvider textureProvider,
        ITargetManager targetManager)
    {
        log.Info($"Starting {PluginConstants.PluginName}...");

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
            textureProvider,
            targetManager);

        // Initialize configuration service (sets static Instance, must be before UI)
        _ = _serviceProvider.GetRequiredService<ConfigurationService>();

        // Initialize UI Manager (triggers subscription to draw events)
        _ = _serviceProvider.GetRequiredService<IUIManager>();

        // Register the /poser command
        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Poser window"
        });

        log.Info($"{PluginConstants.PluginName} started successfully!");
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
        ITextureProvider textureProvider,
        ITargetManager targetManager)
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
        services.AddSingleton(targetManager);

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
        services.AddSingleton<ITimeService, TimeService>();
        services.AddSingleton<IWeatherService, WeatherService>();
        services.AddSingleton<ReferenceImageService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        services.AddSingleton<ILightingService, LightingService>();
        services.AddSingleton<IVirtualCameraService, VirtualCameraService>();

        // Register IPC services (appearance plugins)
        services.AddSingleton<IPenumbraService, PenumbraService>();
        services.AddSingleton<IGlamourerService, GlamourerService>();
        services.AddSingleton<ICustomizePlusService, CustomizePlusService>();

        // Register file services
        services.AddSingleton<IPoseFileService, PoseFileService>();

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
