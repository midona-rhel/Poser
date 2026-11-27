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
/// Lists every service the plugin creates, grouped by feature. These methods
/// only say which services exist; what they do lives in the services
/// themselves. A feature that must start before the window first draws also
/// adds its <see cref="Startable"/> here; <see cref="StartStage"/> sets the
/// order.
///
/// The game and logic services are added before the interface and use no
/// interface types, so tests can add their own replacements after them.
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
        ISeStringEvaluator seStringEvaluator,
        IGameGui gameGui)
    {
        services.AddSingleton(notificationManager);
        services.AddSingleton(seStringEvaluator);
        services.AddSingleton(gameGui);
        services.AddSingleton(pluginInterface);
        services.AddSingleton(log);
        services.AddSingleton(clientState);
        services.AddSingleton(framework);
        services.AddSingleton(objectTable);
        services.AddSingleton(sigScanner);
        // Every hook Poser puts into the game is recorded here, so the check of game functions can read them.
        var hookLedger = new Game.Natives.HookLedger(gameInterop);
        services.AddSingleton(hookLedger);
        services.AddSingleton<IGameInteropProvider>(hookLedger);
        services.AddSingleton<Application.Diagnostics.INativeHealth, Game.Natives.NativeHealthCheck>();
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
        // A new feature can be added here until it gets a method of its own
        // above.
        return services;
    }

    public static IServiceCollection AddPoserPresentation(this IServiceCollection services)
    {
        services.AddFeaturePanes();
        services.AddWindows();
        services.AddUiShell();
        // New interface services can be added here until they get a method of
        // their own above.
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
        // Auto-save needs the GPose service, and the GPose session code needs
        // this final save, so they depend on each other. To break the loop,
        // the auto-save service is only looked up when GPose ends.
        services.AddSingleton<IFinalCapturePort>(sp =>
            new AutoSaveFinalCapturePort(
                () => sp.GetRequiredService<SceneAutoSaveService>()));
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
        services.AddSingleton<StableBindingRegistry>();
        services.AddSingleton<IEntityBindings>(sp => sp.GetRequiredService<StableBindingRegistry>());
        services.AddStartable<StableBindingRegistry>(StartStage.Bindings);
        services.AddSingleton<Application.Scene.SceneGroups>();
        return services;
    }

    private static IServiceCollection AddHistoryFeature(
        this IServiceCollection services)
    {
        // The undo depth setting can change at any time, so undo history reads
        // it for every new step instead of keeping the number from startup.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<ConfigurationService>();
            return new EditHistory(() => configuration.Config.UndoDepth);
        });
        services.AddSingleton<IPoseSnapshotPort, Game.Journal.PoseSnapshotPort>();
        // Created on first use to break a loop: putting a saved pose back
        // goes through services that themselves depend on undo history.
        services.AddSingleton(sp => new System.Lazy<IPoseSnapshotPort>(
            sp.GetRequiredService<IPoseSnapshotPort>));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<UndoJournal>(sp,
            (Func<string, bool>)System.IO.File.Exists));
        // Which item an edit belongs to is looked up for each edit, never when
        // this is created, because the item registry itself records edits here.
        services.AddSingleton(sp => new ValueJournal(sp.GetRequiredService<EditHistory>(),
            owner => Game.Journal.HistoryEntityLookup.Identify(owner, sp.GetRequiredService<IEntityBindings>())));
        services.AddSingleton<global::Poser.Application.Diagnostics.ActionRecorder>();
        // Adding and removing items and moving them share one undo history, in order.
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
        services.AddSingleton<ParentingRuntime>();
        services.AddSingleton<IParentingRuntime>(sp => sp.GetRequiredService<ParentingRuntime>());
        services.AddSingleton<TransformParenting>();
        services.AddSingleton<ITransformParenting>(sp => sp.GetRequiredService<TransformParenting>());
        services.AddSingleton<ITransformRuntimePort>(sp => new ParentedTransformPort(
            sp.GetRequiredService<TransformRuntimePort>(), sp.GetRequiredService<TransformParenting>()));
        services.AddSingleton<ParentingFrameRuntime>();
        services.AddStartable<ParentingFrameRuntime>(StartStage.ParentingFrames);
        services.AddSingleton<TransformGestureService>();
        services.AddSingleton<Application.Lifecycle.ActionGate>();
        services.AddStartable(StartStage.RunningActions, RunningActions.Register);
        services.AddSingleton<ISelectionPlacement, SelectionPlacement>();
        services.AddSingleton<IUndoRunner>(sp => sp.GetRequiredService<TransformGestureService>());
        services.AddSingleton<Game.Posing.ActorColliderCapture>();
        services.AddSingleton<IGroupGateState, GroupGateState>();
        services.AddSingleton<Application.Scene.GroupSteps>();
        // Created on first use to break a loop: undoing a removal puts groups
        // back through the group steps, and the group steps hide, show and
        // remove items through removal.
        services.AddSingleton(sp => new System.Lazy<Application.Scene.GroupSteps>(
            sp.GetRequiredService<Application.Scene.GroupSteps>));
        services.AddSingleton<DisruptiveSteps>();
        services.AddSingleton<TransformCommandService>();
        services.AddSingleton<GroupTransformState>();
        services.AddSingleton<IGroupTransformSource, GroupTransformSource>();
        services.AddSingleton<GroupTransformCoordinator>();
        services.AddSingleton<PoseEditService>();
        services.AddSingleton<PoseStash>();
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
                sp.GetRequiredService<PoseStash>(), sp.GetRequiredService<IPoseEditReads>(),
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
        services.AddSingleton<Game.Cameras.GPoseDepthOfFieldGuard>();
        services.AddStartable<Game.Cameras.GPoseDepthOfFieldGuard>(StartStage.Cameras);
        services.AddSingleton<Application.Presentation.CameraSelectionPolicy>();
        services.AddSingleton<Game.Cameras.CameraTargetControl>();
        services.AddSingleton<Application.Presentation.ICameraTargetControl>(sp =>
            sp.GetRequiredService<Game.Cameras.CameraTargetControl>());
        services.AddSingleton<Game.Cameras.CameraWorkspaceRuntime>();
        services.AddStartable<Game.Cameras.CameraWorkspaceRuntime>(StartStage.CameraWorkspace);
        services.AddSingleton<Game.Cameras.CameraRestRecorder>();
        services.AddStartable<Game.Cameras.CameraRestRecorder>(StartStage.CameraWorkspace);
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
        // What the panes use to save, preview and import poses, backed by game services added elsewhere.
        services.AddSingleton<IPoseFileCapture, ActorPoseCaptureRuntime>();
        services.AddSingleton<IPosePreviewRuntime>(sp => sp.GetRequiredService<Game.Preview.PosePreviewService>());
        services.AddSingleton<IPosePreview>(sp => sp.GetRequiredService<Game.Preview.PosePreviewService>());
        services.AddSingleton<IPoseImportCommands, NativePoseImportService>();
        services.AddSingleton<PoseImportCoordinator>();
        return services;
    }

    private static IServiceCollection AddAnimationFeature(
        this IServiceCollection services)
    {
        // The services below hook the game's animation; AnimationSession puts animation back exactly as it was.
        services.AddSingleton<Game.Animation.AnimationNativeState>();
        services.AddSingleton<Game.Animation.LayerPlayback>();
        services.AddSingleton<Game.Animation.AnimationOverrideStore>();
        services.AddSingleton<Game.Animation.AnimationSpeedRuntimePort>();
        services.AddSingleton<Game.Animation.TimelineSeekService>();
        services.AddSingleton(sp => new Game.Animation.AnimationScrubRuntimePort(
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<Game.Animation.AnimationNativeState>(),
            sp.GetRequiredService<Game.Animation.TimelineSeekService>(),
            sp.GetRequiredService<Game.Animation.AnimationSpeedRuntimePort>()));
        services.AddSingleton<Game.Animation.AnimationSession>(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
            return new Game.Animation.AnimationSession(
                sp.GetRequiredService<Game.Animation.AnimationOverrideStore>(),
                sp.GetRequiredService<Game.Animation.LayerPlayback>(),
                sp.GetRequiredService<Game.Animation.AnimationSpeedRuntimePort>(),
                sp.GetRequiredService<Game.Animation.AnimationNativeState>(),
                sp.GetRequiredService<Game.PosingService>(),
                sp.GetRequiredService<Game.Animation.AnimationScrubRuntimePort>(),
                sp.GetRequiredService<Game.Environment.WorldRenderingService>())
            {
                Trace = message => log.Information($"[AnimState] {message}"),
            };
        });
        services.AddStartable<Game.Animation.AnimationSession>(StartStage.Animation);
        services.AddSingleton<Game.Animation.AnimationSteps>();
        services.AddSingleton<IAnimationActions>(sp => sp.GetRequiredService<Game.Animation.AnimationSteps>());
        services.AddSingleton<IAnimationPlayback>(sp => sp.GetRequiredService<Game.Animation.AnimationSession>());
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
        services.AddSingleton<Application.Companions.ICompanionControl, Game.Companions.CompanionEditor>();
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
        // The item registry is created on first use, to break a loop at
        // startup: StableBindingRegistry needs IActorSpawnService, which needs
        // ISpawnCollectionPort, then PenumbraIpc, then IntegrationActorResolution,
        // which needs StableBindingRegistry again.
        services.AddSingleton(sp => new System.Lazy<Game.Bindings.StableBindingRegistry>(
            sp.GetRequiredService<Game.Bindings.StableBindingRegistry>));
        services.AddSingleton<Game.Integration.IntegrationActorResolution>();
        services.AddSingleton<Application.Integration.IIntegrationResolutionPort>(
            sp => sp.GetRequiredService<Game.Integration.IntegrationActorResolution>());
        services.AddSingleton<Game.Integration.PenumbraIpc>();
        services.AddSingleton<Application.Integration.IPenumbraPort>(
            sp => sp.GetRequiredService<Game.Integration.PenumbraIpc>());
        // The same Penumbra connection, finding actors by where they are in game
        // memory instead of by Poser's own id: a copied actor has no Poser id
        // yet at the moment it needs the original's collection.
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
        services.AddSingleton<IClothingOnlyRuntimePort, Game.Integration.InvisibleSkinService>();
        services.AddSingleton<IInvisibleSkinService, ClothingOnlyControl>();
        services.AddSingleton<Application.Integration.IntegrationOwnership>();
        services.AddSingleton(sp =>
        {
            // The session source tells each MCDF action which GPose session it belongs to.
            var mcdf = new Application.Integration.McdfTransaction(
                sp.GetRequiredService<Application.Integration.IIntegrationResolutionPort>(),
                sp.GetRequiredService<Application.Integration.IPenumbraPort>(),
                sp.GetRequiredService<Application.Integration.IGlamourerPort>(),
                sp.GetRequiredService<Application.Integration.ICustomizePlusPort>(),
                sp.GetRequiredService<Documents.Mcdf.IMcdfFileBoundary>(),
                sp.GetRequiredService<ISessionGenerationSource>(),
                sp.GetRequiredService<Application.Integration.IntegrationOwnership>(),
                sp.GetRequiredService<System.Lazy<IPoseSnapshotPort>>());
            // The MCDF size limits come from the settings, with careful
            // defaults, and are read once here.
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
        // Created after the services it resets. Services are disposed in
        // reverse order, so on unload this runs before they shut down.
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
        // The Library's pose preview. It is not started up front: the Library
        // pane uses it, and it only runs each frame while it is open.
        services.AddSingleton<Game.Preview.PosePreviewService>();
        return services;
    }

    private static IServiceCollection AddSceneOwnership(
        this IServiceCollection services)
    {
        services.AddSingleton<SceneRefreshAndCleanup>();
        services.AddStartable<SceneRefreshAndCleanup>(StartStage.SceneRefreshAndCleanup);
        services.AddSingleton<Game.Scene.PlacementAnchorSource>();
        services.AddSingleton<IPlacementAnchorSource>(sp => sp.GetRequiredService<Game.Scene.PlacementAnchorSource>());
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
        services.AddSingleton<Game.Environment.EnvironmentService>();
        services.AddStartable<Game.Environment.EnvironmentService>(StartStage.Environment);
        services.AddSingleton<Game.Environment.WorldRenderingService>();
        services.AddStartable<Game.Environment.WorldRenderingService>(StartStage.WorldRendering);
        services.AddSingleton<Game.Environment.FestivalService>();
        services.AddSingleton<Game.Environment.EnvironmentControl>();
        services.AddSingleton<IEnvironmentControl>(sp => sp.GetRequiredService<Game.Environment.EnvironmentControl>());
        return services;
    }

    private static IServiceCollection AddSpawnFeature(
        this IServiceCollection services)
    {
        // Taking over actors already in the world and spawning new ones use
        // the same spawn service and the same steps for taking ownership.
        services.AddSingleton<ActorSpawnService>();
        services.AddSingleton<IActorSpawnService>(
            sp => sp.GetRequiredService<ActorSpawnService>());
        services.AddSingleton<WorldActorDiscovery>();
        services.AddSingleton<ISceneCreation, Game.Scene.SceneCreation>();
        services.AddSingleton<PendingSceneCreation>();
        services.AddSingleton<IPendingSceneCreation>(sp => sp.GetRequiredService<PendingSceneCreation>());
        services.AddSingleton<SceneDuplication>();
        services.AddSingleton<ISceneDuplication>(sp => sp.GetRequiredService<SceneDuplication>());
        services.AddSingleton<Game.Scene.SceneCreationTick>();
        services.AddStartable<Game.Scene.SceneCreationTick>(StartStage.SceneCreation);
        services.AddSingleton<global::Poser.Game.Journal.WorldActorSession>();
        services.AddSingleton<ISpawnCatalogService, SpawnCatalogService>();
        return services;
    }

    private static IServiceCollection AddPoseLibraryFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<Application.Library.ILibraryFileOperations, Application.Library.LibraryFileOperations>();
        services.AddSingleton<Application.Library.ICharacterFileLibrary, Application.Library.CharacterFileLibrary>();
        services.AddSingleton(sp => new Application.Library.AutoSaveLibrary(
            sp.GetRequiredService<ConfigurationService>().Config.AutoSave.EnsureRoot(System.IO.Path.Combine(
                sp.GetRequiredService<IDalamudPluginInterface>().GetPluginConfigDirectory(), "AutoSaves")),
            sp.GetRequiredService<SceneAutoSaveService>()));
        services.AddSingleton<Application.Library.ILibrarySceneActions, Application.Library.LibrarySceneActions>();
        services.AddSingleton<Application.Library.IPoseLibraryService>(sp =>
        {
            var config = sp.GetRequiredService<ConfigurationService>();
            // Create Poser's own library folders before the first scan.
            config.Config.Library.EnsureHomeRootsExist();
            var library = new Application.Library.PoseLibraryService(config);
            // Scan once when the library is created. Saving asks for a rescan,
            // and the rescan button picks up files changed outside Poser.
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
        // The map's own objects: the code that finds them in the game, and the
        // service that puts back every object Poser took over. Disposing it
        // when the plugin unloads gives every taken-over object back.
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
        // The code that draws overlays in the game, and the service that adds
        // and removes them. Disposing them when the plugin unloads removes
        // every overlay.
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
        // Expressions are the face controls next to gaze; CommandRouter
        // handles the /poser chat command.
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
        services.AddSingleton<IPlaceService, Game.Environment.PlaceService>();
        services.AddSingleton<AutoSaveRuntime>();
        services.AddStartable<AutoSaveRuntime>(StartStage.AutoSave);

        // One shared list of MCDF checksums, so they are not worked out again for every scene load.
        services.AddSingleton<IMcdfHashIndex>(sp =>
            new McdfHashIndex(() => sp.GetRequiredService<ConfigurationService>().Config.Library.ResolveMcdfRoot()));

        // SceneWorkflow saves and loads scenes; auto-save builds its scene
        // files with the same SceneCaptureService.
        services.AddSingleton<SceneCaptureService>();
        services.AddSceneWorkflow();
        services.AddSingleton<ISceneWorkflow>(sp => sp.GetRequiredService<SceneWorkflow>());
        services.AddStartable<SceneWorkflow>(StartStage.SceneWorkflow);
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<IPluginLog>();
#if DEBUG
            var sceneAutoSaveRoot = global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Resolve(System.IO.Path.Combine(
                sp.GetRequiredService<IDalamudPluginInterface>().GetPluginConfigDirectory(), "SceneAutoSaves"));
#else
            var sceneAutoSaveRoot = System.IO.Path.Combine(
                sp.GetRequiredService<IDalamudPluginInterface>().GetPluginConfigDirectory(), "SceneAutoSaves");
#endif
            return new SceneAutoSaveService(
                sp.GetRequiredService<ConfigurationService>(),
                // Auto-saves include animation when the save dialog's Animation option is on.
                (sceneId, description, onCaptured) => sp.GetRequiredService<SceneCaptureService>().BeginCapture(
                    sceneId, description, onCaptured,
                    sp.GetRequiredService<global::Poser.Application.Scene.SceneLoadPreferences>().SaveOptions.IncludeAnimation),
                () => sp.GetRequiredService<SceneWorkflow>().Busy,
                new SceneAutoSaveStore(sceneAutoSaveRoot,
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
        // The notifications service is added before the interface services.
        // Game and logic services reach it through IUserNotices.
        services.AddSingleton<UserNotices>();
        services.AddSingleton<Application.Presentation.IUserNotices>(sp => sp.GetRequiredService<UserNotices>());
        // Panes are not added here: the main window and each pop-out window
        // create their own (PresentationScope).
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
        // ConfigurationService.Reset replaces the whole settings object, so
        // the presets are read through the service every time.
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
        services.AddSingleton<Application.Selection.EntityLockCommands>();
        services.AddSingleton<ISelectedEntityActions, Game.Selection.SelectedEntityActions>();
        services.AddSingleton<Game.WorldObjects.WorldService>();
        services.AddSingleton<global::Poser.Application.World.IWorldService>(sp => sp.GetRequiredService<Game.WorldObjects.WorldService>());
        services.AddSingleton<Game.WorldObjects.IWorldReleasePort>(sp => sp.GetRequiredService<Game.WorldObjects.WorldService>());
        services.AddSingleton<PoseThumbnailCache>();
        // Holds every reference picture, so disposing it when the plugin
        // unloads frees them all.
        services.AddSingleton<ReferenceImageSession>();
        return services;
    }

    private static IServiceCollection AddWindows(
        this IServiceCollection services)
    {
        // State the windows and panes share while dragging and picking, and
        // the profiler they all report drawing time to: one of each per plugin load.
        services.AddSingleton<global::Poser.UI.Controls.ManipulationState>();
        services.AddSingleton<global::Poser.UI.Controls.BonePick>();
        services.AddSingleton<TransformClipboard>();
        services.AddSingleton<FrameProfiler>();
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
        // The shared state of the interface controls. It is created before
        // the windows and panes at startup, so it is disposed only after them.
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
