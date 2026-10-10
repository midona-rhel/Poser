using Poser.Application.AutoSave;
using Poser.Game.AutoSave;
using Poser.Application.World;
using System;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Animation;
using Poser.Application.Appearance;
using Poser.Application.Companions;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Game.Scene;
using Poser.Game.Transforms;
using Poser.Lifecycle;
using Poser.UI;
using Poser.UI.Composition;

using Poser.Application.Viewport;
using Poser.Documents.AutoSave;
using Poser.Documents.Library;
using Poser.Application.Catalog;
using Poser.Application.Events;
using Poser.Application.Settings;
using Poser.Game.Core;
using Poser.Game.Files;
using Poser.Game.Services;

namespace Poser.Composition;

/// <summary>
/// Explicit composition modules for the plugin executable, arranged as a
/// per-feature registration manifest. These methods only describe ownership;
/// product behavior remains in the registered services. A feature that must
/// run before the UI draws registers its <see cref="Startable"/> here too;
/// <see cref="StartStage"/> is the one start order.
///
/// Runtime modules precede presentation and reference no UI type; tests may
/// append explicit runtime overrides.
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
        ITextureReadbackProvider textureReadback,
        ITargetManager targetManager,
        IChatGui chatGui,
        INotificationManager notificationManager,
        ISeStringEvaluator seStringEvaluator)
    {
        services.AddSingleton(notificationManager);
        services.AddSingleton(seStringEvaluator);
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
        services.AddSingleton(textureReadback);
        services.AddSingleton(targetManager);
        services.AddSingleton(chatGui);
        return services;
    }

    public static IServiceCollection AddPoserRuntime(this IServiceCollection services)
    {
        services.AddConfigurationAndEvents();
        services.AddSessionLifecycle();
        services.AddPosingRuntime();
        services.AddSceneState();
        services.AddHistoryFeature();
        services.AddTransformFeature();
        services.AddCameraFeature();
        services.AddGazeFeature();
        services.AddPoseImportFeature();
        services.AddAnimationFeature();
        services.AddAppearanceFeature();
        services.AddIntegrationFeature();
        services.AddCatalogs();
        services.AddPoseCaptureFeature();
        services.AddSceneOwnership();
        return services;
    }

    public static IServiceCollection AddPoserFeatures(this IServiceCollection services)
    {
        services.AddEnvironmentFeature();
        services.AddSpawnFeature();
        services.AddPoseLibraryFeature();
        services.AddPropFeature();
        services.AddOverlayFeature();
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
        services.AddSingleton<IConfigurationPersistence, HostConfigurationPersistence>();
        services.AddSingleton(sp =>
            new ConfigurationService(sp.GetRequiredService<IConfigurationPersistence>()));
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        return services;
    }

    private static IServiceCollection AddSessionLifecycle(
        this IServiceCollection services)
    {
        // The real cycle: auto-save capture reaches the GPose service, whose
        // coordinator owns this port. The port resolves auto-save at exit.
        services.AddSingleton<IFinalCapturePort>(sp =>
            new AutoSaveFinalCapturePort(
                () => sp.GetRequiredService<IAutoSaveService>()));
        services.AddSingleton<SessionLifecycleCoordinator>();
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
        services.AddSingleton<IKService>();
        services.AddSingleton<Game.Posing.GazePoseFrames>();
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
        services.AddSingleton<ICurrentSelectionEntityReads>(sp =>
            sp.GetRequiredService<SceneSession>());
        services.AddSingleton<StableBindingRegistry>();
        services.AddSingleton<IEntityBindings>(sp => sp.GetRequiredService<StableBindingRegistry>());
        services.AddStartable<StableBindingRegistry>(StartStage.Bindings);
        services.AddSingleton<Application.Scene.SceneGroups>();
        services.AddSingleton<SelectionEntityCommands>();
        return services;
    }

    private static IServiceCollection AddHistoryFeature(
        this IServiceCollection services)
    {
        // The depth is a live setting read per recorded edit, so the history
        // takes the config as a delegate rather than a captured number.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<ConfigurationService>();
            return new TransformHistory(() => configuration.Config.UndoDepth);
        });
        services.AddSingleton<IPoseSnapshotPort, Game.Journal.PoseSnapshotPort>();
        // Lazy: the snapshot port restores through the pose facade, which
        // reaches the gesture service the journal sits above.
        services.AddSingleton(sp => new System.Lazy<IPoseSnapshotPort>(
            sp.GetRequiredService<IPoseSnapshotPort>));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<UndoJournal>(sp,
            (Func<string, bool>)System.IO.File.Exists));
        // The owner lookup reaches the binding registry per recorded edit, never
        // at construction: the registry's own graph writes values through here.
        services.AddSingleton(sp => new ValueJournal(sp.GetRequiredService<TransformHistory>(),
            owner => Game.Journal.HistoryEntityLookup.Identify(owner, sp.GetRequiredService<IEntityBindings>())));
        services.AddSingleton<global::Poser.Application.Diagnostics.ActionRecorder>();
        // Entity lifecycle lands in the transform history, so
        // undo stays one ordered story rather than two.
        services.AddSingleton<Game.Scene.SceneLifecycleHistory>();
        services.AddSingleton<ISceneLifecycleHistory>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<Game.Entities.ILight>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<Game.Entities.ILight>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<Game.Entities.IActor>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<Game.Entities.IActor>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<IWorldObject>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<IWorldObject>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<Game.Entities.IVirtualCamera>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<Game.Entities.IVirtualCamera>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<IPropHandle>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<IPropHandle>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryResolver<IOverlayNode>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        services.AddSingleton<IEntityHistoryBinding<IOverlayNode>>(sp => sp.GetRequiredService<Game.Scene.SceneLifecycleHistory>());
        return services;
    }

    private static IServiceCollection AddTransformFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<TransformRuntimePort>();
        services.AddSingleton<IParentingRuntime, ParentingRuntime>();
        services.AddSingleton<TransformParenting>();
        services.AddSingleton<ITransformParenting>(sp => sp.GetRequiredService<TransformParenting>());
        services.AddSingleton<ITransformRuntimePort>(sp => new ParentedTransformPort(
            sp.GetRequiredService<TransformRuntimePort>(), sp.GetRequiredService<TransformParenting>()));
        services.AddSingleton<ParentingFrameRuntime>();
        services.AddStartable<ParentingFrameRuntime>(StartStage.ParentingFrames);
        services.AddSingleton<TransformGestureService>();
        services.AddSingleton<ISelectionPlacement, SelectionPlacement>();
        services.AddSingleton<IUndoRunner>(sp => sp.GetRequiredService<TransformGestureService>());
        services.AddSingleton<Game.Posing.ActorColliderCapture>();
        services.AddSingleton<IGroupGateState, GroupGateState>();
        services.AddSingleton<Application.Scene.GroupSteps>();
        services.AddSingleton<DisruptiveSteps>();
        services.AddSingleton<TransformCommandService>();
        services.AddSingleton<GroupTransformState>();
        services.AddSingleton<IGroupTransformSource, GroupTransformSource>();
        services.AddSingleton<GroupTransformCoordinator>();
        services.AddSingleton<PoseEditService>();
        services.AddSingleton<PoseTransferService>();
        services.AddSingleton<IPoseEditReads, PoseEditReads>();
        services.AddSingleton<IActorPoseResetRuntime, ActorPoseResetRuntime>();
        services.AddSingleton<IActorResetControl, ActorResetControl>();
        services.AddSingleton<ActorStateSnapshots>();
        services.AddSingleton<IActorStateSnapshots>(sp => sp.GetRequiredService<ActorStateSnapshots>());
        services.AddSingleton<IPoseCommands>(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
            return new PoseCommands(
                sp.GetRequiredService<SceneSession>(), sp.GetRequiredService<PoseEditService>(),
                sp.GetRequiredService<PoseTransferService>(), sp.GetRequiredService<IPoseEditReads>(),
                (description, result) =>
                {
                    if (!result.Success)
                        log.Warning($"Pose edit '{description}' failed: {result.Detail}");
                    else if (!string.IsNullOrEmpty(result.Detail))
                        log.Information($"Pose edit '{description}': {result.Detail}");
                });
        });
        services.AddSingleton<TransformFacade>();
        services.AddSingleton<ITransformFacade>(sp => sp.GetRequiredService<TransformFacade>());
        services.AddSingleton<IPoseInteraction, Game.Posing.PoseInteraction>();
        services.AddSingleton<IIkBake>(sp => sp.GetRequiredService<Game.Posing.IkBakeCapture>());
        services.AddSingleton<IIkConfigurationPort, IkConfigurationPort>();
        return services;
    }

    private static IServiceCollection AddCameraFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<ICameraProjection, CameraService>();
        services.AddSingleton<Application.Input.CameraInputState>();
        services.AddSingleton<IVirtualCameraService, Game.Cameras.VirtualCameraService>();
        services.AddStartable<IVirtualCameraService>(StartStage.Cameras);
        services.AddSingleton<Application.Presentation.CameraSelectionPolicy>();
        services.AddSingleton<Game.Cameras.CameraTargetControl>();
        services.AddSingleton<Application.Presentation.ICameraTargetControl>(sp =>
            sp.GetRequiredService<Game.Cameras.CameraTargetControl>());
        services.AddSingleton<Game.Cameras.CameraWorkspaceRuntime>();
        services.AddStartable<Game.Cameras.CameraWorkspaceRuntime>(StartStage.CameraWorkspace);
        services.AddSingleton<Application.Presentation.ICameraControl, Game.Cameras.CameraControl>();
        services.AddSingleton<Game.Viewport.ViewportProjection>();
        services.AddSingleton<Application.Viewport.IViewportReads>(sp => sp.GetRequiredService<Game.Viewport.ViewportProjection>());
        return services;
    }

    private static IServiceCollection AddGazeFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Application.Gaze.IGazeRuntimePort, Game.Posing.GazeRuntimeAdapter>();
        services.AddSingleton<Application.Gaze.GazeSession>();
        services.AddSingleton<Application.Gaze.IGazeControl>(sp => sp.GetRequiredService<Application.Gaze.GazeSession>());
        services.AddSingleton<GazeService>();
        services.AddStartable<GazeService>(StartStage.Gaze);
        return services;
    }

    private static IServiceCollection AddPoseImportFeature(
        this IServiceCollection services)
    {
        // The surfaces' ports over the runtime classes registered elsewhere.
        services.AddSingleton<IPoseFileCapture, ActorPoseCaptureRuntime>();
        services.AddSingleton<IPosePreviewRuntime>(sp => sp.GetRequiredService<Game.Preview.PosePreviewService>());
        services.AddSingleton<IPosePreview>(sp => sp.GetRequiredService<Game.Preview.PosePreviewService>());
        services.AddSingleton<IPoseImportCommands, NativePoseImportService>();
        services.AddSingleton<PoseImportCoordinator>();
        services.AddSingleton<IPoseImportRuntime, PoseImportRuntime>();
        return services;
    }

    private static IServiceCollection AddAnimationFeature(
        this IServiceCollection services)
    {
        // The port owns native hooks; the session owns exact restoration.
        services.AddSingleton<Game.Animation.AnimationRuntimePort>();
        services.AddSingleton<AnimationSession>(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
            var port = sp.GetRequiredService<Game.Animation.AnimationRuntimePort>();
            return new AnimationSession(port, port, port, port,
                sp.GetRequiredService<IWorldRenderingRuntimePort>())
            {
                Trace = message => log.Information($"[AnimState] {message}"),
            };
        });
        services.AddStartable<AnimationSession>(StartStage.Animation);
        services.AddSingleton<AnimationSteps>();
        services.AddSingleton<IAnimationActions>(sp => sp.GetRequiredService<AnimationSteps>());
        services.AddSingleton<IAnimationPlayback>(sp => sp.GetRequiredService<AnimationSession>());
        services.AddSingleton<IExpressionPreview, Game.Animation.ExpressionPreview>();
        services.AddSingleton<IScenePlaybackControl, ScenePlaybackControl>();
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
        services.AddSingleton<Application.Presentation.IReferenceSkeletonReadPort,
            Game.Presentation.ReferenceSkeletonReadPort>();
        services.AddSingleton<
            Application.Appearance.IModelIdRuntimePort,
            Game.Appearance.ModelIdRuntimePort>();
        services.AddSingleton<Application.Appearance.ActorModelIdSession>();
        services.AddSingleton<Application.Appearance.IActorAppearanceControl, Application.Appearance.ActorAppearanceControl>();
        services.AddSingleton<Application.Presentation.IActorValueRuntime, Game.Presentation.ActorValueRuntime>();
        services.AddSingleton<Application.Presentation.IActorValueControl, Application.Presentation.ActorValueSession>();
        services.AddSingleton<Application.Companions.ICompanionRuntime, Game.Companions.CompanionRuntime>();
        services.AddSingleton<Application.Companions.ICompanionControl, Application.Companions.CompanionSession>();
        services.AddSingleton<IWardrobeControl, WardrobeSession>();
        services.AddSingleton<ICustomizeControl, CustomizeSession>();
        services.AddSingleton<Application.Presentation.IAppearanceColorControl, Game.Journal.AppearanceColorSession>();
        services.AddSingleton<Game.Wardrobe.CustomizeCatalog>();
        services.AddSingleton<ICustomizeCatalog>(sp => sp.GetRequiredService<Game.Wardrobe.CustomizeCatalog>());
        services.AddSingleton<Game.Wardrobe.WardrobeCatalog>();
        services.AddSingleton<IWardrobeCatalog>(sp => sp.GetRequiredService<Game.Wardrobe.WardrobeCatalog>());
        services.AddSingleton<AppearanceCatalogWarmup>();
        services.AddStartable(StartStage.AppearanceCatalogWarmup,
            sp => sp.GetRequiredService<AppearanceCatalogWarmup>().Start());
        services.AddSingleton<Documents.Appearance.ICharacterAppearanceFiles, Documents.Appearance.CharacterAppearanceFiles>();
        services.AddSingleton<Application.Integration.CharacterFileSession>();
        services.AddSingleton<Application.Integration.ICharacterFiles>(sp =>
            sp.GetRequiredService<Application.Integration.CharacterFileSession>());
        services.AddSingleton<Game.Integration.CharacterFilePump>();
        services.AddStartable<Game.Integration.CharacterFilePump>(StartStage.CharacterFiles);
        return services;
    }

    private static IServiceCollection AddIntegrationFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Documents.Mcdf.IMcdfFileBoundary, Documents.Mcdf.McdfFileBoundary>();
        // The lazy registry hand-off breaks the load-time cycle
        // StableBindingRegistry → IActorSpawnService → ISpawnCollectionPort →
        // PenumbraIpc → IntegrationActorResolution → StableBindingRegistry:
        // resolution reads the registry on first use, never during construction.
        services.AddSingleton(sp => new System.Lazy<Game.Bindings.StableBindingRegistry>(
            sp.GetRequiredService<Game.Bindings.StableBindingRegistry>));
        services.AddSingleton<Game.Integration.IntegrationActorResolution>();
        services.AddSingleton<Application.Integration.IIntegrationResolutionPort>(
            sp => sp.GetRequiredService<Game.Integration.IntegrationActorResolution>());
        services.AddSingleton<Game.Integration.PenumbraIpc>();
        services.AddSingleton<Application.Integration.IPenumbraPort>(
            sp => sp.GetRequiredService<Game.Integration.PenumbraIpc>());
        // The same Penumbra client seen by address rather than by stable id:
        // a clone has no binding yet at the moment it needs the source's collection.
        services.AddSingleton<Game.Integration.ISpawnCollectionPort>(
            sp => sp.GetRequiredService<Game.Integration.PenumbraIpc>());
        services.AddSingleton<Game.Integration.GlamourerIpc>();
        services.AddSingleton<Application.Integration.IGlamourerPort>(
            sp => sp.GetRequiredService<Game.Integration.GlamourerIpc>());
        services.AddSingleton<Game.Integration.ISpawnAppearancePort>(
            sp => sp.GetRequiredService<Game.Integration.GlamourerIpc>());
        services.AddSingleton<Game.Integration.CustomizePlusIpc>();
        services.AddSingleton<Application.Integration.ICustomizePlusPort>(
            sp => sp.GetRequiredService<Game.Integration.CustomizePlusIpc>());
        services.AddSingleton<Game.Integration.InvisibleSkinService>();
        services.AddSingleton<IInvisibleSkinService>(sp => sp.GetRequiredService<Game.Integration.InvisibleSkinService>());
        services.AddSingleton<Application.Integration.IntegrationOwnership>();
        services.AddSingleton(sp =>
        {
            // The session source gives every MCDF operation its exact GPose identity.
            var mcdf = new Application.Integration.McdfTransaction(
                sp.GetRequiredService<Application.Integration.IIntegrationResolutionPort>(),
                sp.GetRequiredService<Application.Integration.IPenumbraPort>(),
                sp.GetRequiredService<Application.Integration.IGlamourerPort>(),
                sp.GetRequiredService<Application.Integration.ICustomizePlusPort>(),
                sp.GetRequiredService<Documents.Mcdf.IMcdfFileBoundary>(),
                sp.GetRequiredService<ISessionGenerationSource>(),
                sp.GetRequiredService<Application.Integration.IntegrationOwnership>());
            // The MCDF hard limits are config-backed with conservative
            // defaults; read once at composition.
            var limits = sp.GetRequiredService<Application.Settings.ConfigurationService>()
                .Config.Integration;
            mcdf.Limits = new global::Poser.Documents.Mcdf.McdfLimits(
                limits.McdfMaxTotalBytes,
                limits.McdfMaxFileBytes,
                limits.McdfMaxFileCount,
                limits.McdfMaxGamePathCount);
            return mcdf;
        });
        services.AddSingleton<Application.Integration.IntegrationSelectors>();
        // Created after the ports it resets, so container disposal runs its
        // unload edge before they tear down.
        services.AddSingleton<Application.Integration.IntegrationReset>();
        services.AddStartable<Application.Integration.IntegrationReset>(StartStage.Integration);
        return services;
    }

    private static IServiceCollection AddCatalogs(
        this IServiceCollection services)
    {
        services.AddSingleton<AnimationCatalog>();
        services.AddSingleton<AnimationSceneActions>();
        services.AddSingleton<Game.Animation.AnimationCatalogLoader>();
        services.AddSingleton<IAnimationCatalogLoader>(sp => sp.GetRequiredService<Game.Animation.AnimationCatalogLoader>());
        services.AddSingleton<CompanionCatalog>();
        services.AddSingleton<Game.Companions.CompanionCatalogLoader>();
        services.AddSingleton<ICompanionCatalogLoader>(sp => sp.GetRequiredService<Game.Companions.CompanionCatalogLoader>());
        services.AddSingleton<Application.Appearance.ModelCatalog>();
        services.AddSingleton<Game.Appearance.ModelCatalogLoader>();
        services.AddSingleton<IModelCatalogLoader>(sp => sp.GetRequiredService<Game.Appearance.ModelCatalogLoader>());
        return services;
    }

    private static IServiceCollection AddPoseCaptureFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.Animation.FacialPoseCapture>();
        services.AddSingleton<IFacialPoseCapture>(sp => sp.GetRequiredService<Game.Animation.FacialPoseCapture>());
        services.AddSingleton<Game.Posing.IkBakeCapture>();
        services.AddSingleton<Game.Posing.PoseImportCapture>();
        services.AddSingleton<Func<Game.Posing.PoseImportCapture>>(sp =>
            () => sp.GetRequiredService<Game.Posing.PoseImportCapture>());
        services.AddSingleton<Game.Posing.PoseExportCapture>();
        services.AddSingleton<Application.Animation.IIdleModRuntime, Game.Animation.IdleModRuntime>();
        services.AddSingleton<Application.Animation.IdleModExport>();
        // The pose library's CharaView preview. No force-resolve: the pane
        // holds it, and it only subscribes the framework tick while open.
        services.AddSingleton<Game.Preview.PosePreviewService>();
        return services;
    }

    private static IServiceCollection AddSceneOwnership(
        this IServiceCollection services)
    {
        services.AddSingleton<CleanSceneLifecycle>();
        services.AddStartable<CleanSceneLifecycle>(StartStage.SceneLifecycle);
        services.AddSingleton<Game.Scene.PlacementAnchorSource>();
        services.AddSingleton<IPlacementAnchorSource>(sp => sp.GetRequiredService<Game.Scene.PlacementAnchorSource>());
        services.AddSingleton(sp => new global::Poser.Documents.Files.ObjectPlacementPreferences
        {
            // The session's live choice starts at the configured default.
            Mode = sp.GetRequiredService<ConfigurationService>()
                .Config.DefaultSpawnPlacement,
        });
        services.AddSingleton<TargetSyncService>();
        services.AddStartable<TargetSyncService>(StartStage.TargetSync);
        services.AddSingleton<Game.Input.GPoseMouseTargetHook>();
        services.AddStartable<Game.Input.GPoseMouseTargetHook>(StartStage.MouseTarget);
        services.AddSingleton<EditorState>();
        return services;
    }

    // ----- Features: world, spawning, library, persistence ------------------

    private static IServiceCollection AddEnvironmentFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.Lighting.LightingService>();
        services.AddSingleton<ILightingService>(sp => sp.GetRequiredService<Game.Lighting.LightingService>());
        services.AddStartable<ILightingService>(StartStage.Lighting);
        services.AddSingleton<Application.Presentation.ILightControl, Game.Lighting.LightControl>();
        services.AddSingleton<Application.Posing.IActorColliderCapture>(sp => sp.GetRequiredService<Game.Posing.ActorColliderCapture>());
        services.AddSingleton<Game.Runtime.SceneFramePhaseService>();
        services.AddSingleton<Game.Input.KeyEventHook>();
        services.AddSingleton<global::Poser.Application.Input.IKeyEvents>(
            sp => sp.GetRequiredService<Game.Input.KeyEventHook>());
        services.AddSingleton<IEnvironmentRuntimePort, Game.Environment.EnvironmentService>();
        services.AddStartable<IEnvironmentRuntimePort>(StartStage.Environment);
        services.AddSingleton<EnvironmentControl>();
        services.AddSingleton<IWorldRenderingRuntimePort, Game.Environment.WorldRenderingService>();
        services.AddStartable<IWorldRenderingRuntimePort>(StartStage.WorldRendering);
        services.AddSingleton<IFestivalRuntimePort, Game.Environment.FestivalService>();
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
        services.AddSingleton<ISceneCreation, Game.Scene.SceneCreation>();
        services.AddSingleton<PendingSceneCreation>();
        services.AddSingleton<IPendingSceneCreation>(sp => sp.GetRequiredService<PendingSceneCreation>());
        services.AddSingleton<SceneDuplication>();
        services.AddSingleton<ISceneDuplication>(sp => sp.GetRequiredService<SceneDuplication>());
        services.AddSingleton<Game.Scene.SceneCreationRuntime>();
        services.AddStartable<Game.Scene.SceneCreationRuntime>(StartStage.SceneCreation);
        services.AddSingleton<global::Poser.Game.Journal.WorldActorSession>();
        services.AddSingleton<ISpawnCatalogService, SpawnCatalogService>();
        return services;
    }

    private static IServiceCollection AddPoseLibraryFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Application.Library.ILibraryFileOperations, Application.Library.LibraryFileOperations>();
        services.AddSingleton<Application.Library.AutoSaveLibrary>();
        services.AddSingleton<Application.Library.ILibrarySceneActions, Application.Library.LibrarySceneActions>();
        services.AddSingleton<Application.Library.IPoseLibraryService>(sp =>
        {
            var config = sp.GetRequiredService<ConfigurationService>();
            // Create every configured library root before the first scan.
            config.Config.Library.EnsureHomeRootsExist();
            var library = new Application.Library.PoseLibraryService(config);
            // ONE scan at startup; after it, Poser knows what it saves —
            // every entry save requests its own rescan, and the refresh
            // button covers files changed outside Poser.
            library.RequestScan();
            return library;
        });
        services.AddSingleton<Application.Library.ILibrarySceneSave, Application.Library.LibrarySceneSave>();
        return services;
    }

    private static IServiceCollection AddPropFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Game.PropSpawnService>();
        services.AddSingleton<IPropCatalog>(sp => sp.GetRequiredService<Game.PropSpawnService>());
        services.AddStartable<Game.PropSpawnService>(StartStage.PropSpawns);
        services.AddSingleton<Application.Presentation.ISceneObjectControl, Game.Scene.SceneObjectControl>();
        // The map's own objects: the native walk, and the service that owns
        // every adoption's restore. A singleton because the container's
        // dispose is the unload edge that gives every borrowed object back.
        services.AddSingleton<
            Game.WorldObjects.IWorldObjectPort,
            Game.WorldObjects.NativeWorldObjectPort>();
        services.AddSingleton<Game.WorldObjects.IWorldGraphPort>(sp =>
            sp.GetRequiredService<Game.WorldObjects.IWorldObjectPort>());
        services.AddSingleton<Game.WorldObjects.WorldObjectService>();
        services.AddStartable<Game.WorldObjects.WorldObjectService>(StartStage.WorldObjects);
        services.AddSingleton<Game.WorldObjects.WorldAssetCatalog>();
        services.AddSingleton<IWorldAssetCatalog>(sp => sp.GetRequiredService<Game.WorldObjects.WorldAssetCatalog>());
        return services;
    }

    private static IServiceCollection AddOverlayFeature(
        this IServiceCollection services)
    {
        // The overlay nodes' native seam and the service that owns their
        // lives. Registered as singletons so the container's own dispose is
        // the plugin-unload teardown edge.
        services.AddSingleton<
            Game.Overlays.IOverlayNodePort,
            Game.Overlays.KamiToolKitOverlayPort>();
        services.AddSingleton<Game.Overlays.OverlayNodeService>();
        services.AddSingleton<IOverlayNodeService>(sp => sp.GetRequiredService<Game.Overlays.OverlayNodeService>());
        services.AddStartable<Game.Overlays.OverlayNodeService>(StartStage.OverlayNodes);
        services.AddSingleton<Application.Presentation.IOverlayControl, Game.Overlays.OverlayControl>();
        services.AddSingleton<Game.Overlays.StatusIconCatalog>();
        services.AddSingleton<Application.Presentation.IStatusIconCatalog>(sp =>
            sp.GetRequiredService<Game.Overlays.StatusIconCatalog>());
        return services;
    }

    private static IServiceCollection AddFaceAndDevTools(
        this IServiceCollection services)
    {
        // Expression is the face feature beside gaze; CommandRouter is the
        // /poser chat command.
        services.AddSingleton<IExpressionRuntimePort, ExpressionRuntimePort>();
        services.AddSingleton<IExpressionControl, ExpressionSession>();
        services.AddSingleton<IExpressionService, ExpressionService>();
        services.AddSingleton<CommandRouter>();
        return services;
    }

    private static IServiceCollection AddFilePersistence(
        this IServiceCollection services)
    {
        services.AddSingleton<IPoseFileService, PoseFileService>();
        // One territory-to-place resolution is shared by whole-scene capture
        // and pose auto-save so a recorded place means the same thing in both
        // documents.
        services.AddSingleton<IPlaceService, Game.Environment.PlaceService>();
        // Not a cycle break (the final-capture port is that): the capture
        // reaches the actor graph only when it captures, so starting
        // auto-save first does not build the binding registry ahead of its
        // own start stage.
        services.AddSingleton<IPoseAutoSaveCapture>(sp => new PoseAutoSaveCapturePort(
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<IActorManager>,
            sp.GetRequiredService<ISkeletonService>,
            sp.GetRequiredService<IBonePosingService>,
            sp.GetRequiredService<IPoseFileService>,
            sp.GetRequiredService<IPlaceService>()));
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
            var configuration = sp.GetRequiredService<ConfigurationService>();
            var root = configuration.Config.AutoSave.EnsureRoot(System.IO.Path.Combine(
                sp.GetRequiredService<IDalamudPluginInterface>().GetPluginConfigDirectory(), "AutoSaves"));
            return new AutoSaveService(sp.GetRequiredService<IPoseAutoSaveCapture>(), configuration,
                new PoseAutoSaveStore(root, message => log.Error(message),
                    message => log.Info(message), message => log.Debug(message)),
                message => log.Error(message), message => log.Debug(message));
        });
        services.AddSingleton<IAutoSaveService>(sp => sp.GetRequiredService<AutoSaveService>());
        services.AddSingleton<AutoSaveRuntime>();
        services.AddStartable<AutoSaveRuntime>(StartStage.AutoSave);

        // The checksum index over the MCDF home. ONE instance: its whole
        // value is the digests it remembers between scene loads, and a
        // per-resolve copy would re-read the library every time.
        services.AddSingleton<IMcdfHashIndex>(sp =>
            new McdfHashIndex(() => sp.GetRequiredService<ConfigurationService>().Config.Library.ResolveMcdfRoot()));

        // SceneWorkflow owns the scene transaction; autosave reuses its
        // capture and store through SceneCaptureService.
        services.AddSingleton<SceneCaptureService>();
        services.AddSceneWorkflow();
        services.AddSingleton<ISceneWorkflow>(sp => sp.GetRequiredService<SceneWorkflow>());
        services.AddStartable<SceneWorkflow>(StartStage.SceneWorkflow);
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
            return new SceneAutoSaveService(
                sp.GetRequiredService<ConfigurationService>(),
                sp.GetRequiredService<SceneCaptureService>().BeginCapture,
                () => sp.GetRequiredService<SceneWorkflow>().Busy,
                new SceneAutoSaveStore(System.IO.Path.Combine(
                    sp.GetRequiredService<IDalamudPluginInterface>().GetPluginConfigDirectory(), "SceneAutoSaves"),
                    message => log.Error(message)),
                message => log.Warning(message));
        });
        services.AddStartable(StartStage.SceneAutoSave, sp =>
            sp.GetRequiredService<AutoSaveRuntime>().StartSceneSnapshots(
                sp.GetRequiredService<SceneAutoSaveService>()));
        return services;
    }

    // ----- Presentation: panes, windows, shell ------------------------------

    private static IServiceCollection AddFeaturePanes(
        this IServiceCollection services)
    {
        // The one transient-message channel every surface below speaks
        // through, registered ahead of them all; runtime owners reach it
        // through the Application port.
        services.AddSingleton<UserNotices>();
        services.AddSingleton<Application.Presentation.IUserNotices>(sp => sp.GetRequiredService<UserNotices>());
        // Pane types are never registered here: the main window's graph and
        // each pop-out's lease own theirs (PresentationScope).
        services.AddSingleton<MainPresentation>();
        services.AddSingleton(sp => sp.GetRequiredService<MainPresentation>().Content);
        services.AddSingleton(sp => sp.GetRequiredService<MainPresentation>().Rail);
        services.AddSingleton<IPropertiesContentFactory, PropertiesContentFactory>();
        services.AddSingleton<EntityRemovalDialog>();
        services.AddSingleton<global::Poser.Diagnostics.IssueReportService>();
        services.AddSingleton<global::Poser.Application.Diagnostics.IIssueReports>(sp => sp.GetRequiredService<global::Poser.Diagnostics.IssueReportService>());
        services.AddSingleton<global::Poser.UI.Controls.IssueReportModal>();
#if DEBUG
        services.AddSingleton<global::Poser.Bridge.DebugBridge>();
        services.AddStartable<global::Poser.Bridge.DebugBridge>(StartStage.DebugBridge);
#endif
        services.AddSingleton<SceneLoadPreferences>();
        services.AddSingleton<PoseLibraryPane>();
        services.AddSingleton<BoneMapEditorRegistry>();
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
        services.AddSingleton<Application.World.WorldAcquisitionControl>();
        services.AddSingleton<Application.World.IWorldAcquisitionControl>(sp =>
            sp.GetRequiredService<Application.World.WorldAcquisitionControl>());
        services.AddSingleton<WorldAdoptionSource>();
        services.AddSingleton<Application.Scene.IActorSceneControl, Game.Scene.ActorSceneControl>();
        services.AddSingleton<EntityActions>();
        services.AddSingleton<ISelectionEntityCommandPort, Game.Selection.SelectionEntityCommandPort>();
        services.AddSingleton<Game.WorldObjects.WorldService>();
        services.AddSingleton<global::Poser.Application.World.IWorldService>(sp => sp.GetRequiredService<Game.WorldObjects.WorldService>());
        services.AddSingleton<Game.WorldObjects.IWorldReleasePort>(sp => sp.GetRequiredService<Game.WorldObjects.WorldService>());
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
        services.AddSingleton(new UiBuildIdentity(BuildMetadata.Branch, BuildMetadata.Commit));
        services.AddSingleton(sp => new global::Poser.Application.Settings.ReleaseNotesSession(
            sp.GetRequiredService<ConfigurationService>(), typeof(ServiceRegistration).Assembly.GetName().Version!));
        services.AddSingleton<global::Poser.UI.Views.ReleaseNotesView>();
        // The widgets' state for this plugin load; the UI manager owns it.
        services.AddSingleton(sp =>
        {
            var textures = sp.GetRequiredService<ITextureProvider>();
            var log = sp.GetRequiredService<IPluginLog>();
            return new global::Poser.UI.Widgets.UiContext(
                (pixels, width, height) =>
                {
                    var wrap = textures.CreateFromRaw(
                        RawImageSpecification.Rgba32(width, height),
                        pixels,
                        "Poser widget texture");
                    return ((nint)wrap.Handle.Handle, wrap);
                },
                message => log.Debug(message));
        });
        services.AddSingleton<UiWindowSet>();
        services.AddSingleton<IUIManager, UIManager>();
        return services;
    }
}
