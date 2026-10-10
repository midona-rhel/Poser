using Poser.Application.World;
using Poser.Application.AutoSave;
using Poser.Game.AutoSave;
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
using Poser.Game;
using Poser.Game.Posing;
using Poser.Game.Scene;
using Poser.Services;
using Poser.Application.Scene;
using Poser.UI;
using Poser.UI.Widgets;
using Poser.Domain;
using Poser.Domain.Posing.BoneInfo;

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
        ITextureReadbackProvider textureReadback,
        ITargetManager targetManager,
        IChatGui chatGui,
        INotificationManager notificationManager,
        ISeStringEvaluator seStringEvaluator)
    {
        log.Info($"Starting {PluginConstants.PluginName}...");

        _commandManager = commandManager;
        using var startup = new StartupCleanup(error =>
            log.Error(error, "Failed-startup cleanup failed"));
        BoneInfoService.Initialize(message => log.Warning(message));
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
            textureReadback,
            targetManager,
            chatGui,
            notificationManager,
            seStringEvaluator);
        startup.OnFailure(_serviceProvider.Dispose);
        log.Debug("Load stage: configuration");
        var configuration =
            _serviceProvider.GetRequiredService<ConfigurationService>();
        // The widgets draw through one context, installed as it is built;
        // the UI manager owns it from here and disposes it on unload.
        _ = _serviceProvider.GetRequiredService<UiContext>();
        ThemeSelection.Apply(
            configuration.Config.UI.Theme,
            configuration.Config.UI.AccentIndex);
        // Install the saved surface recipe before any UI draws.
        FloatingSurface.ConfigureEffects(
            configuration.Config.UI.FillOpacity,
            configuration.Config.UI.BackdropBlur);
        // Every feature registered its own startables; this is the one place
        // they run, in StartStage order, before any UI draws.
        Startables.StartAll(_serviceProvider, log);
        // The other polarity's fonts warm on a second atlas, so the atlas
        // the UI draws with is never rebuilt once it is up: the rebuild's
        // landing frame was the one frame the whole UI went missing.
        _standbyFontAtlas = pluginInterface.UiBuilder.CreateFontAtlas(
            Dalamud.Interface.ManagedFontAtlas.FontAtlasAutoRebuildMode.Async,
            debugName: "Poser standby fonts");
        startup.OnFailure(_standbyFontAtlas.Dispose);
        startup.OnFailure(FontRegistry.Dispose);
        FontRegistry.Register(
            pluginInterface.UiBuilder.FontAtlas,
            System.IO.Path.Combine(
                pluginInterface.AssemblyLocation.DirectoryName ?? ".",
                "Data", "Fonts"),
            _standbyFontAtlas);
        log.Debug("Load stage: UI manager");
        var uiManager = _serviceProvider.GetRequiredService<IUIManager>();
        // Unwinds before the fonts registered above.
        startup.OnFailure(uiManager.Dispose);
        if (!_commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Poser."
        }))
            throw new InvalidOperationException("The /poser command is already registered.");
        startup.OnFailure(() => _commandManager.RemoveHandler(CommandName));

        log.Info($"{PluginConstants.PluginName} started successfully!");
        startup.Complete();
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
        ITextureReadbackProvider textureReadback,
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
                textureReadback,
                targetManager,
                chatGui,
                notificationManager,
                seStringEvaluator)
            .AddPoserRuntime()
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
        var lifecycle = serviceProvider.GetService<SessionLifecycleCoordinator>();
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
        _serviceProvider.GetRequiredService<global::Poser.Lifecycle.AppearanceCatalogWarmup>().Dispose();
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
                // Draw stops before any resource it draws with is released;
                // the provider's later dispose of the manager is a no-op.
                _serviceProvider.GetRequiredService<IUIManager>().Dispose();
                FontRegistry.Dispose();
                _standbyFontAtlas.Dispose();
            });
    }
}
