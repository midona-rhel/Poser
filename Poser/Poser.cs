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
        IChatGui chatGui,
        INotificationManager notificationManager)
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
            chatGui,
            notificationManager);

        // Initialize configuration service (sets static Instance, must be before UI)
        log.Debug("Load stage: configuration");
        var configuration =
            _serviceProvider.GetRequiredService<ConfigurationService>();
        ThemeSelection.Apply(
            configuration.Config.UI.Theme,
            configuration.Config.UI.AccentIndex);

        // Activate periodic auto-save. Final GPose capture is requested by the
        // lifecycle coordinator before the legacy exit event is published.
        log.Debug("Load stage: auto-save");
        _ = _serviceProvider.GetRequiredService<IAutoSaveService>();

        // The whole-scene snapshot has framework subscriptions as its only
        // activity — the same lazy-singleton hazard: resolve it or it never
        // ticks.
        // The scene auto-save resolution drags the whole scene runtime graph
        // in behind it; each link is resolved by name here so a construction
        // that blocks names its own constructor in the log.
        log.Debug("Load link: prop spawns");
        _ = _serviceProvider.GetRequiredService<Game.PropSpawnService>();
        log.Debug("Load link: overlay nodes");
        _ = _serviceProvider.GetRequiredService<Game.Overlays.OverlayNodeService>();
        log.Debug("Load link: world objects");
        _ = _serviceProvider.GetRequiredService<Game.WorldObjects.WorldObjectService>();
        log.Debug("Load link: lighting");
        _ = _serviceProvider.GetRequiredService<ILightingService>();
        log.Debug("Load link: cameras");
        _ = _serviceProvider.GetRequiredService<IVirtualCameraService>();
        log.Debug("Load link: environment");
        _ = _serviceProvider.GetRequiredService<IEnvironmentService>();
        log.Debug("Load link: bindings");
        _ = _serviceProvider.GetRequiredService<Game.Bindings.StableBindingRegistry>();
        log.Debug("Load link: animation");
        _ = _serviceProvider.GetRequiredService<Application.Animation.AnimationSession>();
        log.Debug("Load link: gaze");
        _ = _serviceProvider.GetRequiredService<IGazeService>();
        log.Debug("Load link: integration");
        _ = _serviceProvider.GetRequiredService<Application.Integration.ActorIntegrationSession>();
        log.Debug("Load link: world rendering");
        _ = _serviceProvider.GetRequiredService<IWorldRenderingService>();
        log.Debug("Load link: scene workflow");
        _ = _serviceProvider.GetRequiredService<SceneWorkflow>();
        log.Debug("Load stage: scene auto-save");
        _ = _serviceProvider.GetRequiredService<SceneAutoSaveService>();

        // Activate the clean scene owner before constructing presentation.
        // Singleton registration is lazy: without resolving this service its
        // actor/skeleton subscriptions never run and SceneSession stays empty.
        log.Debug("Load stage: scene lifecycle");
        _ = _serviceProvider.GetRequiredService<CleanSceneLifecycle>();

        // Target sync is another lazy singleton with framework subscriptions
        // as its only activity; resolve it or it never ticks.
        log.Debug("Load stage: target sync");
        _ = _serviceProvider.GetRequiredService<TargetSyncService>();

        // Create the active theme's complete typography matrix before any
        // presentation surface can measure with a fallback face.
        FontRegistry.Register(pluginInterface.UiBuilder.FontAtlas);

        // Icons and panel shadows upload through the same provider; the wrap
        // is each cache entry's keepalive, so replacement/eviction releases it.
        Func<byte[], int, int, (nint, IDisposable?)> textureUploader = (pixels, width, height) =>
        {
            var wrap = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(width, height),
                pixels,
                "Crystarium icon");
            return ((nint)wrap.Handle.Handle, wrap);
        };
        Crystarium.IconTextureUploader = textureUploader;
        Crystarium.PanelShadowTextureUploader = textureUploader;

        // Dalamud provides real backdrop blur for the retained glass surfaces.
        Crystarium.FloatingSurface.BackdropBlurAvailable = true;

        // Initialize UI Manager (triggers subscription to draw events)
        log.Debug("Load stage: UI manager");
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
        IChatGui chatGui,
        INotificationManager notificationManager)
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
                chatGui,
                notificationManager)
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
                Crystarium.PanelShadowTextureUploader = null;
                FontRegistry.Dispose();
            });
    }
}
