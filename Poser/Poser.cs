using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Composition;
using Poser.Config;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Game;
using Poser.Game.Scene;
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
        ITargetManager targetManager,
        IChatGui chatGui)
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
            targetManager,
            chatGui);

        // Initialize configuration service (sets static Instance, must be before UI)
        _ = _serviceProvider.GetRequiredService<ConfigurationService>();

        // Activate the clean scene owner before constructing presentation.
        // Singleton registration is lazy: without resolving this service its
        // actor/skeleton subscriptions never run and SceneSession stays empty.
        _ = _serviceProvider.GetRequiredService<CleanSceneLifecycle>();

        // Create the active theme's complete typography matrix before any
        // presentation surface can measure with a fallback face.
        FontRegistry.Register(pluginInterface.UiBuilder.FontAtlas);

        // Dalamud provides real backdrop blur for the retained glass surfaces.
        Crystarium.FloatingSurface.BackdropBlurAvailable = true;

        // Initialize UI Manager (triggers subscription to draw events)
        _ = _serviceProvider.GetRequiredService<IUIManager>();

        // Register the /poser command
        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Poser. Use \"/poser test\" for the focused in-game validation harness."
        });

        log.Info($"{PluginConstants.PluginName} started successfully!");
    }

    private void OnCommand(string command, string args)
    {
        _serviceProvider.GetRequiredService<CommandRouter>().Handle(args);
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
        ITargetManager targetManager,
        IChatGui chatGui)
    {
        return new ServiceCollection()
            .AddDalamudDependencies(
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
                targetManager,
                chatGui)
            .AddPoserCore()
            .AddPoserFeatures()
            .AddPoserPresentation()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(CommandName);
        FontRegistry.Dispose();
        _serviceProvider.Dispose();
    }
}
