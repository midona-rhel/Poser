using System;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Lifecycle;
using Poser.Composition;
using Poser.Config;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Game;
using Poser.Game.Posing;
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
        var configuration =
            _serviceProvider.GetRequiredService<ConfigurationService>();
        ThemeSelection.Apply(
            configuration.Config.UI.Theme,
            configuration.Config.UI.AccentIndex);

        // Activate periodic auto-save. Final GPose capture is requested by the
        // lifecycle coordinator before the legacy exit event is published.
        _ = _serviceProvider.GetRequiredService<IAutoSaveService>();

        // The whole-shot snapshot has framework subscriptions as its only
        // activity — the same lazy-singleton hazard: resolve it or it never
        // ticks.
        _ = _serviceProvider.GetRequiredService<SceneAutoSaveService>();

        // Activate the clean scene owner before constructing presentation.
        // Singleton registration is lazy: without resolving this service its
        // actor/skeleton subscriptions never run and SceneSession stays empty.
        _ = _serviceProvider.GetRequiredService<CleanSceneLifecycle>();

        // Target sync is another lazy singleton with framework subscriptions
        // as its only activity; resolve it or it never ticks.
        _ = _serviceProvider.GetRequiredService<TargetSyncService>();

        // Create the active theme's complete typography matrix before any
        // presentation surface can measure with a fallback face.
        FontRegistry.Register(pluginInterface.UiBuilder.FontAtlas);

        // Icons bake to a texture once and draw as one quad after that; the
        // wrap is the keepalive, so the cache disposing an entry releases it.
        Crystarium.IconTextureUploader = (pixels, width, height) =>
        {
            var wrap = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(width, height),
                pixels,
                "Crystarium icon");
            return ((nint)wrap.Handle.Handle, wrap);
        };

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

    internal static void DisposeProviderAfterFrameworkExit(
        ServiceProvider serviceProvider,
        IFramework framework,
        IGPoseService gpose,
        IPluginLog log,
        Action cleanup)
    {
        var lifecycle = serviceProvider.GetService<ISessionLifecycleCoordinator>();
        try
        {
            // Unload is the same lifecycle edge as ordinary GPose exit. Marshal
            // the live graph read to the framework thread and join it before
            // provider teardown. A failed/canceled hop is logged as evidence;
            // it never claims that final capture completed.
            if (framework.IsInFrameworkUpdateThread)
                gpose.ExitForUnload();
            else
                framework.RunOnFrameworkThread(gpose.ExitForUnload)
                    .GetAwaiter()
                    .GetResult();
        }
        catch (Exception ex)
        {
            log.Error($"GPose unload lifecycle dispatch failed: {ex}");
        }
        finally
        {
            // A failed or canceled framework hop cannot claim that the exit
            // edge ran. Close token admission before cleanup can dispose any
            // provider-owned collaborator, so no late GPose entry can reopen
            // the graph during teardown.
            serviceProvider.GetService<PoseImportCapture>()?
                .InvalidateForHostTeardown(
                    "Pose import invalidated because framework unload dispatch did not complete its drain.");
            lifecycle?.InvalidateForUnload();
            try
            {
                cleanup();
            }
            finally
            {
                serviceProvider.Dispose();
            }
        }
    }

    public void Dispose()
    {
        var framework = _serviceProvider.GetRequiredService<IFramework>();
        var gpose = _serviceProvider.GetRequiredService<IGPoseService>();
        var log = _serviceProvider.GetRequiredService<IPluginLog>();
        DisposeProviderAfterFrameworkExit(
            _serviceProvider,
            framework,
            gpose,
            log,
            () =>
            {
                _commandManager.RemoveHandler(CommandName);
                Crystarium.IconTextureUploader = null;
                FontRegistry.Dispose();
            });
    }
}
