using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;
using KamiToolKit.UiOverlay;
using Poser.Domain.Presentation;

namespace Poser.Game.Overlays;

/// <summary>
/// Draws overlays with KamiToolKit, the library that adds nodes to the game's
/// UI. The library starts on the first <see cref="Create"/>. If it fails to
/// start, no further overlays are created until Poser loads again. Every node
/// built here is kept until it is freed: <see cref="Destroy"/> uses this to
/// ignore a node freed before, and unloading uses it to free the nodes still
/// drawn.
/// </summary>
public sealed class KamiToolKitOverlayNodeDrawer : IOverlayNodeDrawer
{
    private readonly IDalamudPluginInterface _plugin;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;

    /// <summary>Every node built here and not yet freed, compared by
    /// reference.</summary>
    private readonly HashSet<OverlayShapeNode> _live =
        new(ReferenceEqualityComparer.Instance);

    private OverlayController? _controller;
    private bool _initialized;
    private bool _failed;
    private bool _disposed;

    public KamiToolKitOverlayNodeDrawer(
        IDalamudPluginInterface plugin,
        ITextureProvider textures,
        IPluginLog log)
    {
        _plugin = plugin;
        _textures = textures;
        _log = log;
    }

    public bool IsAvailable => !_disposed && !_failed;

    public Action<object, Vector2>? Moved { get; set; }

    public object? Create(OverlayNodeState state)
    {
        if (!TryGetController(out var controller))
            return null;

        OverlayShapeNode node = state.Kind switch
        {
            OverlayNodeKind.Balloon => new BalloonShapeNode(),
            OverlayNodeKind.Status => new StatusShapeNode(),
            _ => new TalkShapeNode(),
        };
        // The node reports only its new position. This passes the node along
        // with it, because OverlayNodeService finds the overlay by its node.
        node.Moved = position => Moved?.Invoke(node, position);
        try
        {
            Write(node, state);
            controller.AddNode(node);
            _live.Add(node);
            return node;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"KamiToolKitOverlayNodeDrawer: attaching a {state.Kind} node failed: {ex.Message}");
            // Drawing the node or adding it to the game failed. Free it here:
            // the caller gets null and so has nothing to free.
            TryDispose(node);
            return null;
        }
    }

    public void Apply(object node, OverlayNodeState state)
    {
        if (node is not OverlayShapeNode shape || !_live.Contains(shape))
            return;
        Write(shape, state);
    }

    public void Destroy(object node)
    {
        // A node that is already freed is left alone.
        if (node is not OverlayShapeNode shape || !_live.Remove(shape))
            return;
        try
        {
            _controller!.RemoveNode(shape);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayNodeDrawer: detaching a node failed: {ex.Message}");
        }
        TryDispose(shape);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Take every node back from the game and free it, then free the
        // controller, then the library: the reverse of the order they were made in. In any
        // other order the game's UI keeps pointing at memory Poser has freed.
        foreach (var shape in _live)
        {
            try
            {
                _controller!.RemoveNode(shape);
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"KamiToolKitOverlayNodeDrawer: detaching a node during teardown failed: {ex.Message}");
            }
            TryDispose(shape);
        }
        _live.Clear();

        try
        {
            _controller?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayNodeDrawer: disposing the overlay controller failed: {ex.Message}");
        }
        _controller = null;

        if (!_initialized)
            return;
        _initialized = false;
        try
        {
            KamiToolKitLibrary.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayNodeDrawer: disposing the node library failed: {ex.Message}");
        }
    }

    /// <summary>Returns the controller, starting the library and the controller
    /// on the first call. Returns false after <see cref="Dispose"/> or after a
    /// failed start; a failed start is not tried again.</summary>
    private bool TryGetController(out OverlayController controller)
    {
        controller = null!;
        if (_disposed || _failed)
            return false;
        if (_controller is { } existing)
        {
            controller = existing;
            return true;
        }
        try
        {
            KamiToolKitLibrary.Initialize(_plugin);
            _initialized = true;
            _controller = new OverlayController();
            controller = _controller;
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            _log.Error(
                $"KamiToolKitOverlayNodeDrawer: the node library is unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Looks up a status line's icon file, then redraws the node.</summary>
    private void Write(OverlayShapeNode node, OverlayNodeState state)
    {
        if (state.Kind == OverlayNodeKind.Status)
            node.IconPath = IconPath(state.StatusIconId);
        node.State = state;
    }

    /// <summary>The game file of an icon, or empty for icon 0. Asks Dalamud
    /// first. If Dalamud returns no path or throws, builds the path the way the
    /// game names its icon files: ui/icon/{group}/{id}_hr1.tex.</summary>
    private string IconPath(uint iconId)
    {
        if (iconId == 0)
            return string.Empty;
        try
        {
            if (_textures.TryGetIconPath(
                    new GameIconLookup(iconId), out var path))
                return path;
        }
        catch (Exception)
        {
            // Build the path below instead.
        }
        uint group = iconId / 1000 * 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ui/icon/{group:D6}/{iconId:D6}_hr1.tex");
    }

    private void TryDispose(OverlayShapeNode node)
    {
        // Stop the node reporting drags first, so a node being freed can't
        // report a drag for an overlay that is already gone.
        node.Moved = null;
        try
        {
            node.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayNodeDrawer: freeing a node failed: {ex.Message}");
        }
    }
}
