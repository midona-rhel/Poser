using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Animation;
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
using Poser.Services;
using Poser.UI;
using Poser.UI.Composition;

namespace Poser.Composition;

/// <summary>
/// Explicit composition modules for the plugin executable. These methods only
/// describe ownership; product behavior remains in the registered services.
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
        IChatGui chatGui)
    {
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
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());

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

        services.AddSingleton<SelectionSession>();
        services.AddSingleton<SceneSession>();
        services.AddSingleton<StableBindingRegistry>();
        services.AddSingleton<ITransformRuntimePort, TransformRuntimePort>();
        services.AddSingleton<TransformHistory>();
        services.AddSingleton<TransformGestureService>();
        services.AddSingleton<TransformCommandService>();
        services.AddSingleton<PoseEditService>();
        services.AddSingleton<PoseTransferService>();
        services.AddSingleton<CleanTransformFacade>();
        services.AddSingleton<Game.Viewport.ViewportProjection>();
        services.AddSingleton<CleanPoseFacade>();
        services.AddSingleton<IIkConfigurationPort, IkConfigurationPort>();
        // Animation joins the clean core, not the legacy feature block:
        // the port owns the hooks and every address, the session owns
        // stable-id state and restoration.
        services.AddSingleton<Game.Animation.AnimationRuntimePort>();
        services.AddSingleton<IAnimationRuntimePort>(
            sp => sp.GetRequiredService<Game.Animation.AnimationRuntimePort>());
        services.AddSingleton<AnimationSession>();

        services.AddSingleton<Game.Presentation.PresentationRuntimePort>();
        services.AddSingleton<Application.Presentation.IPresentationRuntimePort>(
            sp => sp.GetRequiredService<Game.Presentation.PresentationRuntimePort>());
        services.AddSingleton<Application.Presentation.ActorPresentationSession>();
        services.AddSingleton<Application.Integration.IMcdfFileBoundary, Game.Mcdf.McdfFileBoundary>();
        services.AddSingleton<Game.Integration.IntegrationRuntimePort>();
        services.AddSingleton<Application.Integration.IIntegrationRuntimePort>(
            sp => sp.GetRequiredService<Game.Integration.IntegrationRuntimePort>());
        services.AddSingleton<Application.Integration.ActorIntegrationSession>(sp =>
        {
            var session = new Application.Integration.ActorIntegrationSession(
                sp.GetRequiredService<Application.Integration.IIntegrationRuntimePort>(),
                sp.GetRequiredService<Application.Integration.IMcdfFileBoundary>());
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
        services.AddSingleton<AnimationCatalog>();
        services.AddSingleton<AnimationSceneActions>();
        services.AddSingleton<Game.Animation.AnimationCatalogLoader>();
        services.AddSingleton<Game.Animation.FacialPoseCapture>();
        services.AddSingleton<Game.Posing.IkBakeCapture>();
        services.AddSingleton<Game.Posing.PoseImportCapture>();
        services.AddSingleton<Game.Posing.PoseExportCapture>();
        // The pose library's CharaView preview. No force-resolve: the pane
        // holds it, and it only subscribes the framework tick while open.
        services.AddSingleton<Game.Preview.PosePreviewService>();
        services.AddSingleton<CleanSceneLifecycle>();
        services.AddSingleton<TargetSyncService>();
        services.AddSingleton<IEditorState, EditorState>();
        return services;
    }

    public static IServiceCollection AddPoserFeatures(this IServiceCollection services)
    {
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<ILightingService, Game.Lighting.LightingService>();
        services.AddSingleton<IEnvironmentService, Game.Environment.EnvironmentService>();
        services.AddSingleton<IWorldRenderingService, Game.Environment.WorldRenderingService>();
        services.AddSingleton<IFestivalService, Game.Environment.FestivalService>();
        services.AddSingleton<IActorSpawnService, ActorSpawnService>();
        services.AddSingleton<ISpawnCatalogService, SpawnCatalogService>();
        services.AddSingleton<Library.IPoseLibraryService, Library.PoseLibraryService>();
        services.AddSingleton<Game.PropSpawnService>();
        services.AddSingleton<IGazeService, GazeService>();
        services.AddSingleton<ILiveTestService, LiveTestService>();
        services.AddSingleton<IExpressionService, ExpressionService>();
        services.AddSingleton<CommandRouter>();

        services.AddSingleton<IPoseFileService, PoseFileService>();
        services.AddSingleton<ILightFileService, LightFileService>();
        // Factory-registered on purpose: the scene services are handed over as
        // factories so constructing the auto-save does NOT construct them. They
        // wipe their state from their own GPose-exit handlers, and the EventBus
        // dispatches in subscription order — taking them as plain constructor
        // arguments would subscribe them first and leave the exit snapshot
        // nothing to write.
        services.AddSingleton<IAutoSaveService>(sp => new AutoSaveService(
            sp.GetRequiredService<IPluginLog>(),
            sp.GetRequiredService<IFramework>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IGPoseService>(),
            sp.GetRequiredService<IActorManager>,
            sp.GetRequiredService<ISkeletonService>,
            sp.GetRequiredService<IBonePosingService>,
            sp.GetRequiredService<IPoseFileService>,
            sp.GetRequiredService<ConfigurationService>(),
            sp.GetRequiredService<IDalamudPluginInterface>()));
        return services;
    }

    public static IServiceCollection AddPoserPresentation(this IServiceCollection services)
    {
        services.AddSingleton<ExpressionInspectorSection>();
        services.AddSingleton<PoseFileInspectorSection>();
        services.AddSingleton<PoseInspectorPane>();
        services.AddSingleton<PoseRailPane>();
        services.AddSingleton<AnimationPane>();
        services.AddSingleton<AppearancePane>();
        services.AddSingleton<LightPane>();
        services.AddSingleton<EnvironmentPane>();
        services.AddSingleton<PoseLibraryPane>();
        services.AddSingleton<GraphicalBonePane>();
        services.AddSingleton<SkeletonOverlayPresentation>();
        services.AddSingleton<PoseThumbnailCache>();

        services.AddSingleton<SkeletonOverlayWindow>();
        services.AddSingleton<GizmoOverlayWindow>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsWindow>();
        services.AddSingleton<SpawnBrowserWindow>();

        services.AddSingleton<UiWindowSet>();
        services.AddSingleton<IUIManager, UIManager>();
        return services;
    }
}
