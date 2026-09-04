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
    private readonly Dalamud.Interface.ManagedFontAtlas.IFontAtlas _standbyFontAtlas;
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
        INotificationManager notificationManager,
        ISeStringEvaluator seStringEvaluator)
    {
        log.Info($"Starting {PluginConstants.PluginName}...");

        _commandManager = commandManager;
        BoneInfoService.Initialize(log);
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
            notificationManager,
            seStringEvaluator);
        log.Debug("Load stage: configuration");
        var configuration =
            _serviceProvider.GetRequiredService<ConfigurationService>();
        ThemeSelection.Apply(
            configuration.Config.UI.Theme,
            configuration.Config.UI.AccentIndex);
        // Install the saved surface recipe before any UI draws.
        Crystarium.FloatingSurface.ConfigureEffects(
            configuration.Config.UI.FillOpacity,
            configuration.Config.UI.BackdropBlur);
        // Resolving these lazy singletons activates their subscriptions in runtime order before UI draws.
        log.Debug("Load stage: auto-save");
        _ = _serviceProvider.GetRequiredService<IAutoSaveService>();
        log.Debug("Load link: prop spawns");
        _ = _serviceProvider.GetRequiredService<Game.PropSpawnService>();
        log.Debug("Load link: overlay nodes");
        _ = _serviceProvider.GetRequiredService<Game.Overlays.OverlayNodeService>();
        log.Debug("Load link: world objects");
        _ = _serviceProvider.GetRequiredService<Game.WorldObjects.WorldObjectService>();
        log.Debug("Load link: lighting");
        _ = _serviceProvider.GetRequiredService<ILightingService>();
        _ = _serviceProvider.GetRequiredService<Game.Scene.SceneGroupsLifetime>();
        log.Debug("Load link: cameras");
        var virtualCameras =
            _serviceProvider.GetRequiredService<IVirtualCameraService>();
        // The animation anchor pumps from the render seam when the camera
        // scene-update hook stands; the overlay draw remains its fallback.
        if (virtualCameras is Game.Cameras.VirtualCameraService cameraHooks
            && cameraHooks.SceneUpdateHookLive)
        {
            var anchoredObjects = _serviceProvider
                .GetRequiredService<Game.WorldObjects.WorldObjectService>();
            cameraHooks.AfterSceneUpdate =
                anchoredObjects.HoldPausedAnimations;
            anchoredObjects.AnchorPumpedFromRender = true;
        }
        log.Debug("Load link: environment");
        _ = _serviceProvider.GetRequiredService<IEnvironmentService>();
        log.Debug("Load link: bindings");
        _ = _serviceProvider.GetRequiredService<Game.Bindings.StableBindingRegistry>();
        log.Debug("Load link: animation");
        _ = _serviceProvider.GetRequiredService<Application.Animation.AnimationSession>();
#if DEBUG
        log.Debug("Load link: debug bridge");
        _ = _serviceProvider.GetRequiredService<global::Poser.Bridge.DebugBridge>();
#endif
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
        log.Debug("Load stage: scene lifecycle");
        _ = _serviceProvider.GetRequiredService<CleanSceneLifecycle>();
        global::Poser.UI.Crystarium.Log = message =>
            _serviceProvider.GetRequiredService<
                Dalamud.Plugin.Services.IPluginLog>().Debug(message);
        log.Debug("Load stage: target sync");
        _ = _serviceProvider.GetRequiredService<TargetSyncService>();
        // The other polarity's fonts warm on a second atlas, so the atlas
        // the UI draws with is never rebuilt once it is up: the rebuild's
        // landing frame was the one frame the whole UI went missing.
        _standbyFontAtlas = pluginInterface.UiBuilder.CreateFontAtlas(
            Dalamud.Interface.ManagedFontAtlas.FontAtlasAutoRebuildMode.Async,
            debugName: "Poser standby fonts");
        FontRegistry.Register(
            pluginInterface.UiBuilder.FontAtlas,
            System.IO.Path.Combine(
                pluginInterface.AssemblyLocation.DirectoryName ?? ".",
                "Data", "Fonts"),
            _standbyFontAtlas);
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
        Crystarium.FloatingSurface.BackdropBlurAvailable = true;
        log.Debug("Load stage: UI manager");
        _ = _serviceProvider.GetRequiredService<IUIManager>();
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
        INotificationManager notificationManager,
        ISeStringEvaluator seStringEvaluator)
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
                notificationManager,
                seStringEvaluator)
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
                _standbyFontAtlas.Dispose();
            });
    }
}
