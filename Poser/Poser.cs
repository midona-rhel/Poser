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
        var configService = _serviceProvider.GetRequiredService<ConfigurationService>();

        // Bootstrap Norvrandt's font registry — pre-builds IFontHandles for theme typo sizes
        // (11/13/16/22/32) so ElementStyle.FontSize actually applies.
        FontRegistry.Register(pluginInterface.UiBuilder.FontAtlas);

        // Dalamud provides real backdrop blur for the retained glass surfaces.
        GlassChrome.BackdropBlurAvailable = true;

        // Bridge Poser's UIConfiguration into Crystarium's stylesheet theme.
        // First sync runs on the first draw frame (Resolve() touches ImGui style
        // colors, which is unsafe outside an ImGui frame).
        ThemeBridge.Initialize(pluginInterface, configService);

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
        ThemeBridge.Dispose();
        FontRegistry.Dispose();
        _serviceProvider.Dispose();
    }
}
