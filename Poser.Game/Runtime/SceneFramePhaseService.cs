using System;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Game.WorldObjects;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace Poser.Game.Runtime;

/// <summary>Owns the post-scene/pre-render phase and its one framework fallback.</summary>
public sealed unsafe class SceneFramePhaseService : IDisposable
{
    // Brio's scene-update seam. Anchors must run after native animation and
    // before the free-camera matrix replacement, or either can flicker.
    private const string Signature =
        "48 ?? ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? F6 81 F0 ?? ?? ?? ?? 48 8B ??";
    private delegate nint SceneUpdate(SceneCamera* camera);
    internal delegate void CameraPhase(SceneCamera* camera);
    internal event CameraPhase? CameraUpdate;
    private readonly Hook<SceneUpdate>? _hook;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly Action _anchor;
    private bool _disposed;
    public bool RenderHookAvailable { get; }

    public SceneFramePhaseService(ISigScanner scanner, IGameInteropProvider hooks,
        IFramework framework, IPluginLog log, WorldObjectService objects)
    {
        _framework = framework;
        _log = log;
        _anchor = objects.HoldPausedAnimations;
        using var startup = new StartupCleanup(error =>
            log.Error(error, "Scene frame activation cleanup failed"));
        try
        {
            using var activation = new StartupCleanup(error =>
                log.Error(error, "Scene hook cleanup failed"));
            var hook = hooks.HookFromAddress<SceneUpdate>(scanner.ScanText(Signature), Detour);
            activation.OnFailure(hook.Dispose);
            // Publish before enabling: the native callback can run immediately.
            _hook = hook;
            hook.Enable();
            startup.OnFailure(hook.Dispose);
            RenderHookAvailable = true;
            activation.Complete();
        }
        catch (Exception error)
        {
            _hook = null;
            log.Warning($"Scene render phase unavailable; using framework anchors: {error.Message}");
        }
        startup.OnFailure(() => framework.Update -= OnFrameworkUpdate);
        framework.Update += OnFrameworkUpdate;
        startup.Complete();
    }

    internal SceneFramePhaseService(IFramework framework, IPluginLog log,
        Action anchor, bool renderHookAvailable)
    {
        _framework = framework;
        _log = log;
        _anchor = anchor;
        RenderHookAvailable = renderHookAvailable;
        framework.Update += OnFrameworkUpdate;
    }

    private nint Detour(SceneCamera* camera)
    {
        var result = _hook!.Original(camera);
        RunScenePhase(camera);
        return result;
    }

    internal void RunScenePhase(SceneCamera* camera)
    {
        if (_disposed) return;
        Anchor();
        try { CameraUpdate?.Invoke(camera); }
        catch (Exception error) { _log.Error(error, "Scene camera phase failed"); }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!_disposed && !RenderHookAvailable) Anchor();
    }

    private void Anchor()
    {
        try { _anchor(); }
        catch (Exception error) { _log.Error(error, "Scene animation anchor phase failed"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        _hook?.Dispose();
        CameraUpdate = null;
    }
}
