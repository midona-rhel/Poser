using System;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Animation;
using Poser.Application.Companions;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Config;
using Poser.Core;
using Poser.Files;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Game.Scene;
using Poser.Game.Transforms;
using Poser.Game.Validation;
using Poser.Library;
using Poser.Lifecycle;
using Poser.Services;
using Poser.UI;
using Poser.UI.Composition;

namespace Poser.Composition;

/// <summary>
/// Explicit composition modules for the plugin executable, arranged as a
/// per-feature registration manifest. These methods only describe ownership;
/// product behavior remains in the registered services.
///
/// Order contract: the public module methods, their call order
/// (Dalamud, core, features, presentation), and each module's content are
/// load-bearing — the lifecycle contract suite composes AddPoserCore +
/// AddPoserFeatures alone and appends its own overrides afterward, relying on
/// last-registration-wins. Inside a module no service type is registered
/// twice and nothing resolves IEnumerable&lt;T&gt; over these registrations,
/// so intra-module order carries no container meaning; the feature methods
/// preserve the original registration sequence verbatim regardless.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection AddDalamudDependencies(
        this IServiceCollection services,
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
        services.AddSingleton(notificationManager);
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
        services.AddSingleton(chatGui);
        return services;
    }

    public static IServiceCollection AddPoserCore(this IServiceCollection services)
    {
        services.AddConfigurationAndEvents();
        services.AddSessionLifecycle();
        services.AddPosingRuntime();
        services.AddSceneState();
        services.AddTransformFeature();
        services.AddAnimationFeature();
        services.AddAppearanceFeature();
        services.AddIntegrationFeature();
        services.AddCatalogs();
        services.AddPoseCaptureFeature();
        services.AddSceneOwnership();
        // Feature-pending: new core registrations land here until they move
        // into (or become) a feature method above.
        return services;
    }

    public static IServiceCollection AddPoserFeatures(this IServiceCollection services)
    {
        services.AddEnvironmentAndCameras();
        services.AddSpawnFeature();
        services.AddPoseLibraryFeature();
        services.AddPropFeature();
        services.AddFaceAndDevTools();
        services.AddFilePersistence();
        // Feature-pending: new feature registrations land here until they move
        // into (or become) a feature method above.
        return services;
    }

    public static IServiceCollection AddPoserPresentation(this IServiceCollection services)
    {
        services.AddFeaturePanes();
        services.AddWindows();
        services.AddUiShell();
        // Feature-pending: new presentation registrations land here until they
        // move into (or become) a feature method above.
        return services;
    }

    // ----- Core: configuration, lifecycle, posing runtime -------------------

    private static IServiceCollection AddConfigurationAndEvents(
        this IServiceCollection services)
    {
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        return services;
    }

    private static IServiceCollection AddSessionLifecycle(
        this IServiceCollection services)
    {
        services.AddSingleton<IFinalCapturePort>(sp =>
            new AutoSaveFinalCapturePort(
                () => sp.GetRequiredService<IAutoSaveService>()));
        services.AddSingleton<SessionLifecycleCoordinator>();
        services.AddSingleton<ISessionLifecycleCoordinator>(sp =>
            sp.GetRequiredService<SessionLifecycleCoordinator>());
        services.AddSingleton<ISessionGenerationSource>(sp =>
            sp.GetRequiredService<SessionLifecycleCoordinator>());
        return services;
    }

    private static IServiceCollection AddPosingRuntime(
        this IServiceCollection services)
    {
        services.AddSingleton<IGPoseService, GPoseService>();
        services.AddSingleton<IActorManager, ActorManager>();
        services.AddSingleton<PosingService>();
        services.AddSingleton<IPosingService>(
            sp => sp.GetRequiredService<PosingService>());
        services.AddSingleton<ISkeletonService, SkeletonService>();
        services.AddSingleton<IIKService, IKService>();
        services.AddSingleton<BonePosingService>();
        services.AddSingleton<IBonePosingService>(
            sp => sp.GetRequiredService<BonePosingService>());
        return services;
    }

    private static IServiceCollection AddSceneState(
        this IServiceCollection services)
    {
        services.AddSingleton<SelectionSession>();
        services.AddSingleton<SceneSession>();
        services.AddSingleton<StableBindingRegistry>();
        services.AddSingleton<IEntityBindings>(sp => sp.GetRequiredService<StableBindingRegistry>());
        services.AddSingleton<Application.Scene.SceneGroups>();
        services.AddSingleton<Game.Scene.SceneGroupsLifetime>();
        return services;
    }

    private static IServiceCollection AddTransformFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<ITransformRuntimePort, TransformRuntimePort>();
        // The depth is a live setting read per recorded edit, so the history
        // takes the config as a delegate rather than a captured number.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<ConfigurationService>();
            return new TransformHistory(() => configuration.Config.UndoDepth);
        });
        services.AddSingleton<TransformGestureService>();
        services.AddSingleton<IUndoRunner>(sp => sp.GetRequiredService<TransformGestureService>());
        services.AddSingleton<ActorDisruptionEpochs>();
        services.AddSingleton<IActorStateKeySource, ActorStateKeySource>();
        services.AddSingleton<IPoseSnapshotPort, Game.Journal.PoseSnapshotPort>();
        // Lazy: the snapshot port restores through the pose facade, which
        // reaches the gesture service the journal sits above.
        services.AddSingleton(sp => new System.Lazy<IPoseSnapshotPort>(
            sp.GetRequiredService<IPoseSnapshotPort>));
        services.AddSingleton<JournalContexts>();
        services.AddSingleton(sp => new UndoJournal(
            sp.GetRequiredService<TransformHistory>(),
            sp.GetRequiredService<IUndoRunner>(),
            sp.GetRequiredService<IActorStateKeySource>(),
            sp.GetRequiredService<System.Lazy<IPoseSnapshotPort>>(),
            System.IO.File.Exists,
            sp.GetRequiredService<global::Poser.UI.UserNotices>().Note));
        services.AddSingleton<ValueJournal>();
        services.AddSingleton<Game.Journal.WorldObjectSession>();
        services.AddSingleton<Game.Journal.PropSession>();
        services.AddSingleton<Game.Journal.OverlaySession>();
        services.AddSingleton<Game.Journal.LightSession>();
        services.AddSingleton<Game.Journal.CameraSession>();
        services.AddSingleton<Game.Journal.EnvironmentSession>();
        services.AddSingleton<Game.Journal.ActorValueSession>();
        services.AddSingleton<Game.Journal.ExpressionSession>();
        services.AddSingleton<Game.Journal.GazeSession>();
        services.AddSingleton<AnimationSteps>();
        services.AddSingleton<Application.Scene.GroupSteps>();
        services.AddSingleton<Game.Journal.DisruptiveSteps>();
        services.AddSingleton(sp => new Game.Journal.EntitySessions(
            sp.GetRequiredService<Game.Journal.ActorValueSession>(),
            sp.GetRequiredService<Game.Journal.LightSession>(),
            sp.GetRequiredService<Game.Journal.CameraSession>(),
            sp.GetRequiredService<Game.Journal.PropSession>(),
            sp.GetRequiredService<Game.Journal.WorldObjectSession>(),
            sp.GetRequiredService<Game.Journal.OverlaySession>()));
        services.AddSingleton<TransformCommandService>();
        services.AddSingleton<PoseEditService>();
        services.AddSingleton<PoseTransferService>();
        services.AddSingleton<CleanTransformFacade>();
        // Entity lifecycle lands in the transform history, so
        // undo stays one ordered story rather than two.
        services.AddSingleton<Game.Scene.SceneLifecycleHistory>();
        services.AddSingleton<ISceneLifecycleHistory>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<Game.Viewport.ViewportProjection>();
        services.AddSingleton<Application.Viewport.IViewportReads>(sp => sp.GetRequiredService<Game.Viewport.ViewportProjection>());
        services.AddSingleton<CleanPoseFacade>();
        services.AddSingleton<IIkConfigurationPort, IkConfigurationPort>();
        return services;
    }

    private static IServiceCollection AddAnimationFeature(
        this IServiceCollection services)
    {
        // The port owns native hooks; the session owns exact restoration.
        services.AddSingleton<Game.Animation.AnimationRuntimePort>();
        services.AddSingleton<IAnimationRuntimePort>(
            sp => sp.GetRequiredService<Game.Animation.AnimationRuntimePort>());
        services.AddSingleton<AnimationSession>(sp =>
            new AnimationSession(sp.GetRequiredService<IAnimationRuntimePort>())
            {
                Trace = message => sp
                    .GetRequiredService<Dalamud.Plugin.Services.IPluginLog>()
                    .Information($"[AnimState] {message}"),
            });
        return services;
    }

    private static IServiceCollection AddAppearanceFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.Presentation.PresentationRuntimePort>();
        services.AddSingleton<Application.Presentation.IPresentationRuntimePort>(
            sp => sp.GetRequiredService<Game.Presentation.PresentationRuntimePort>());
        services.AddSingleton<Application.Presentation.ActorPresentationSession>();
        services.AddSingleton<
            Application.Presentation.ICustomizeReadRuntimePort,
            Game.Presentation.CustomizeReadRuntimePort>();
        services.AddSingleton<
            Application.Appearance.IModelIdRuntimePort,
            Game.Appearance.ModelIdRuntimePort>();
        services.AddSingleton<Application.Appearance.ActorModelIdSession>();
        return services;
    }

    private static IServiceCollection AddIntegrationFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Application.Integration.IMcdfFileBoundary, Game.Mcdf.McdfFileBoundary>();
        // The lazy registry hand-off breaks the load-time cycle
        // StableBindingRegistry → IActorSpawnService → ISpawnCollectionPort →
        // IntegrationRuntimePort → StableBindingRegistry: the port resolves
        // the registry on first use, never during construction.
        services.AddSingleton(sp => new System.Lazy<Game.Bindings.StableBindingRegistry>(
            sp.GetRequiredService<Game.Bindings.StableBindingRegistry>));
        services.AddSingleton<Game.Integration.IntegrationRuntimePort>();
        services.AddSingleton<Game.Integration.InvisibleSkinService>();
        services.AddSingleton<Application.Integration.IIntegrationRuntimePort>(
            sp => sp.GetRequiredService<Game.Integration.IntegrationRuntimePort>());
        // The same port seen by address rather than by stable id: a clone has
        // no binding yet at the moment it needs the source's collection.
        services.AddSingleton<Game.Integration.ISpawnCollectionPort>(
            sp => sp.GetRequiredService<Game.Integration.IntegrationRuntimePort>());
        services.AddSingleton<Application.Integration.ActorIntegrationSession>(sp =>
        {
            // The session owns the concrete McdfTransaction; the session
            // source gives every MCDF operation its exact GPose identity.
            var session = new Application.Integration.ActorIntegrationSession(
                sp.GetRequiredService<Application.Integration.IIntegrationRuntimePort>(),
                sp.GetRequiredService<Application.Integration.IMcdfFileBoundary>(),
                sp.GetRequiredService<ISessionGenerationSource>());
            // The MCDF hard limits are config-backed with conservative
            // defaults; read once at composition.
            var limits = sp.GetRequiredService<Config.ConfigurationService>()
                .Config.Integration;
            session.Limits = new global::Poser.Domain.Integration.McdfLimits(
                limits.McdfMaxTotalBytes,
                limits.McdfMaxFileBytes,
                limits.McdfMaxFileCount,
                limits.McdfMaxGamePathCount);
            return session;
        });
        return services;
    }

    private static IServiceCollection AddCatalogs(
        this IServiceCollection services)
    {
        services.AddSingleton<AnimationCatalog>();
        services.AddSingleton<AnimationSceneActions>();
        services.AddSingleton<Game.Animation.AnimationCatalogLoader>();
        services.AddSingleton<CompanionCatalog>();
        services.AddSingleton<Game.Companions.CompanionCatalogLoader>();
        services.AddSingleton<Application.Appearance.ModelCatalog>();
        services.AddSingleton<Game.Appearance.ModelCatalogLoader>();
        return services;
    }

    private static IServiceCollection AddPoseCaptureFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.Animation.FacialPoseCapture>();
        services.AddSingleton<Game.Posing.IkBakeCapture>();
        services.AddSingleton<Game.Posing.PoseImportCapture>();
        services.AddSingleton<Func<Game.Posing.IPoseImportLifecycleControl>>(sp =>
            () => sp.GetRequiredService<Game.Posing.PoseImportCapture>());
        services.AddSingleton<Game.Posing.PoseExportCapture>();
        // The pose library's CharaView preview. No force-resolve: the pane
        // holds it, and it only subscribes the framework tick while open.
        services.AddSingleton<Game.Preview.PosePreviewService>();
        return services;
    }

    private static IServiceCollection AddSceneOwnership(
        this IServiceCollection services)
    {
        services.AddSingleton<CleanSceneLifecycle>();
        services.AddSingleton<Game.Scene.PlacementAnchorSource>();
        services.AddSingleton(sp => new global::Poser.Files.ObjectPlacementPreferences
        {
            // The session's live choice starts at the configured default.
            Mode = sp.GetRequiredService<ConfigurationService>()
                .Config.DefaultSpawnPlacement,
        });
        services.AddSingleton<TargetSyncService>();
        services.AddSingleton<IEditorState, EditorState>();
        return services;
    }

    // ----- Features: world, spawning, library, persistence ------------------

    private static IServiceCollection AddEnvironmentAndCameras(
        this IServiceCollection services)
    {
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<ILightingService, Game.Lighting.LightingService>();
        services.AddSingleton<IVirtualCameraService, Game.Cameras.VirtualCameraService>();
        services.AddSingleton<IEnvironmentService, Game.Environment.EnvironmentService>();
        services.AddSingleton<IWorldRenderingService, Game.Environment.WorldRenderingService>();
        services.AddSingleton<IFestivalService, Game.Environment.FestivalService>();
        return services;
    }

    private static IServiceCollection AddSpawnFeature(
        this IServiceCollection services)
    {
        // The concrete spawn service is registered once and forwarded: the
        // world-actor discovery funnels its clones through the same accepted
        // ownership transaction (no second spawner).
        services.AddSingleton<ActorSpawnService>();
        services.AddSingleton<IActorSpawnService>(
            sp => sp.GetRequiredService<ActorSpawnService>());
        services.AddSingleton<WorldActorDiscovery>();
        services.AddSingleton<Application.Actors.IWorldActorReadPort>(
            sp => sp.GetRequiredService<WorldActorDiscovery>());
        services.AddSingleton<ISpawnCatalogService, SpawnCatalogService>();
        return services;
    }

    private static IServiceCollection AddPoseLibraryFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Library.IPoseLibraryService>(sp =>
        {
            var config = sp.GetRequiredService<ConfigurationService>();
            // Create every configured library root before the first scan.
            config.Config.Library.EnsureHomeRootsExist();
            var library = new Library.PoseLibraryService(config);
            // ONE scan at startup; after it, Poser knows what it saves —
            // every entry save requests its own rescan, and the refresh
            // button covers files changed outside Poser.
            library.RequestScan();
            return library;
        });
        return services;
    }

    private static IServiceCollection AddPropFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.PropSpawnService>();
        // The overlay nodes' native seam and the service that owns their
        // lives. Registered as singletons so the container's own dispose is
        // the plugin-unload teardown edge.
        services.AddSingleton<
            Game.Overlays.IOverlayNodePort,
            Game.Overlays.KamiToolKitOverlayPort>();
        services.AddSingleton<Game.Overlays.OverlayNodeService>();
        services.AddSingleton<Game.Overlays.StatusIconCatalog>();
        // The map's own objects: the native walk, and the service that owns
        // every adoption's restore. A singleton for the same reason the
        // overlay port is one — the container's dispose is the unload edge
        // that gives every borrowed object back.
        services.AddSingleton<
            Game.WorldObjects.IWorldObjectPort,
            Game.WorldObjects.NativeWorldObjectPort>();
        services.AddSingleton<Game.WorldObjects.WorldObjectService>();
        services.AddSingleton<Game.WorldObjects.WorldAssetCatalog>();
        services.AddSingleton<Game.StainCatalog>();
        return services;
    }

    private static IServiceCollection AddFaceAndDevTools(
        this IServiceCollection services)
    {
        // Gaze and expression are the face features; LiveTestService is the
        // in-game validation harness and CommandRouter the /poser dev bridge.
        services.AddSingleton<IGazeService, GazeService>();
        services.AddSingleton<ILiveTestService, LiveTestService>();
        services.AddSingleton<IExpressionService, ExpressionService>();
        services.AddSingleton<CommandRouter>();
        return services;
    }

    private static IServiceCollection AddFilePersistence(
        this IServiceCollection services)
    {
        services.AddSingleton<IPoseFileService, PoseFileService>();
        services.AddSingleton<ILightFileService, LightFileService>();
        services.AddSingleton<ICameraFileService, CameraFileService>();
        // One territory-to-place resolution is shared by whole-scene capture
        // and pose auto-save so a recorded place means the same thing in both
        // documents.
        services.AddSingleton<IPlaceService, Game.Environment.PlaceService>();
        // Lazy resolution breaks the final-capture construction cycle.
        services.AddSingleton<IAutoSaveService>(sp => new AutoSaveService(
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<IFramework>(),
            sp.GetRequiredService<IGPoseService>(),
            sp.GetRequiredService<IActorManager>,
            sp.GetRequiredService<ISkeletonService>,
            sp.GetRequiredService<IBonePosingService>,
            sp.GetRequiredService<IPoseFileService>,
            sp.GetRequiredService<ConfigurationService>(),
            sp.GetRequiredService<IPlaceService>(),
            sp.GetRequiredService<IDalamudPluginInterface>()));

        // The checksum index over the MCDF home. ONE instance: its whole
        // value is the digests it remembers between scene loads, and a
        // per-resolve copy would re-read the library every time.
        services.AddSingleton<IMcdfHashIndex>(sp =>
            new McdfHashIndex(sp.GetRequiredService<ConfigurationService>()));

        // SceneWorkflow owns the scene transaction; autosave reuses its
        // capture and store through SceneCaptureService.
        services.AddSingleton<SceneCaptureService>();
        services.AddSingleton<SceneWorkflow>();
        services.AddSingleton(sp => new SceneAutoSaveService(
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<IFramework>(),
            sp.GetRequiredService<IGPoseService>(),
            sp.GetRequiredService<ConfigurationService>(),
            sp.GetRequiredService<SceneCaptureService>(),
            sp.GetRequiredService<SceneWorkflow>(),
            sp.GetRequiredService<IDalamudPluginInterface>()));
        return services;
    }

    // ----- Presentation: panes, windows, shell ------------------------------

    private static IServiceCollection AddFeaturePanes(
        this IServiceCollection services)
    {
        // The one transient-message channel every surface below speaks
        // through, registered ahead of them all.
        services.AddSingleton<UserNotices>();
        services.AddSingleton<ExpressionInspectorSection>();
        services.AddSingleton<PoseFileInspectorSection>();
        services.AddSingleton<PoseInspectorPane>();
        services.AddSingleton<SelectionSection>();
        services.AddSingleton<PoseRailPane>();
        services.AddSingleton<AnimationPane>();
#if DEBUG
        services.AddSingleton<global::Poser.Bridge.DebugBridge>();
#endif
        services.AddSingleton<CompanionSection>();
        services.AddSingleton<AppearancePane>();
        services.AddSingleton<PropsPane>();
        services.AddSingleton<WorldObjectsPane>();
        services.AddSingleton<global::Poser.UI.Controls.EntityNameModal>();
        services.AddSingleton<OverlayPane>();
        services.AddSingleton<LightPane>();
        services.AddSingleton<CameraPane>();
        services.AddSingleton<EnvironmentPane>();
        services.AddSingleton<SceneLoadPreferences>();
        services.AddSingleton<PoseLibraryPane>();
        services.AddSingleton<ScenePane>();
        services.AddSingleton<GraphicalBonePane>();
        services.AddSingleton<SkeletonOverlayPresentation>();
        // ConfigurationService.Reset replaces the configuration instance,
        // so the preset store is reached through the service on every call.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<ConfigurationService>();
            return new BoneVisibilityPresetService(
                sp.GetRequiredService<SkeletonOverlayPresentation>(),
                () => configuration.Config,
                configuration.Save);
        });
        services.AddSingleton<WorldAdoptionSource>();
        services.AddSingleton<PoseThumbnailCache>();
        // Owns every reference picture's texture, so the container's own
        // dispose is what releases them at plugin teardown.
        services.AddSingleton<ReferenceImageSession>();
        return services;
    }

    private static IServiceCollection AddWindows(
        this IServiceCollection services)
    {
        services.AddSingleton<SkeletonOverlayWindow>();
        services.AddSingleton<GizmoOverlayWindow>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsWindow>();
        services.AddSingleton<SpawnBrowserWindow>();
        return services;
    }

    private static IServiceCollection AddUiShell(
        this IServiceCollection services)
    {
        services.AddSingleton<UiWindowSet>();
        services.AddSingleton<IUIManager, UIManager>();
        return services;
    }
}
