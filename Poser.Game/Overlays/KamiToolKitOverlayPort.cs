using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiSeStringRenderer;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KamiToolKit;
using KamiToolKit.UiOverlay;
using Poser.Domain.Presentation;

namespace Poser.Game.Overlays;

/// <summary>
/// The production <see cref="IOverlayNodePort"/>: KamiToolKit's UI-overlay
/// controller, which is the same library and the same controller Ktisis uses
/// (<c>Ktisis/Services/Game/OverlayService.cs:43,52,93,102-103</c>).
///
/// <para>WHY A LIBRARY. Attaching a node subtree to the game's UI means
/// building <c>AtkResNode</c>s by hand, walking the right addon's node list,
/// fixing up sibling/parent pointers, and undoing all of it in the exact
/// reverse order before the addon is finalised. Getting the undo wrong crashes
/// the renderer rather than throwing. KamiToolKit owns that dance, and it is
/// the ONLY dependency this feature adds.</para>
///
/// <para>LIFETIME. The library has one global init and one global teardown, so
/// this port owns both: init is deferred to the first node (a session that
/// never stages one never touches the game's UI at all) and teardown runs from
/// <see cref="Dispose"/>. Between them the controller holds every live node,
/// and the port keeps its own set of them so a dispose can take back anything
/// the service above it never heard about — a node created for a create that
/// failed before its handle existed, for instance.</para>
///
/// <para>A failure to initialise is REMEMBERED as unavailability rather than
/// retried per node: a library that could not attach once will not attach on
/// the next click, and the create affordances read as unavailable instead of
/// silently doing nothing.</para>
/// </summary>
public sealed class KamiToolKitOverlayPort : IOverlayNodePort
{
    private readonly IDalamudPluginInterface _plugin;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;

    /// <summary>Every node this port created and has not yet freed. Reference
    /// identity: two nodes are the same node only when they are.</summary>
    private readonly HashSet<object> _live =
        new(ReferenceEqualityComparer.Instance);

    private OverlayController? _controller;
    private bool _initialized;
    private bool _failed;
    private bool _disposed;

    /// <summary>The SeString renderer's font. It defaults to the CURRENT
    /// ImGui font, and text renders during the Atk update — outside any
    /// ImGui frame, where that font is empty — so the port carries its own
    /// Axis handle and locks it per render. Lazy: a session that never
    /// stages text builds no atlas.</summary>
    private IFontAtlas? _fontAtlas;
    private IFontHandle? _axisFont;

    public KamiToolKitOverlayPort(
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
        // Bound to the node, not to the port's listener: the node knows nothing
        // about tokens, and the token is what the service upstream indexes by.
        node.Moved = position => Moved?.Invoke(node, position);
        node.RenderText = RenderText;
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
                $"KamiToolKitOverlayPort: attaching a {state.Kind} node failed: {ex.Message}");
            // The node was built but never took: free it here, because nothing
            // above this line has a token to free it with.
            _live.Remove(node);
            TryDispose(node);
            return null;
        }
    }

    public void Apply(object node, OverlayNodeState state)
    {
        if (_disposed || node is not OverlayShapeNode shape ||
            !_live.Contains(shape))
            return;
        Write(shape, state);
    }

    public void Destroy(object node)
    {
        // Not ours, or already freed: inert, per the teardown contract.
        if (node is not OverlayShapeNode shape || !_live.Remove(shape))
            return;
        try
        {
            _controller?.RemoveNode(shape);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayPort: detaching a node failed: {ex.Message}");
        }
        TryDispose(shape);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Detach every node BEFORE the controller goes, then the controller,
        // then the library — the exact reverse of the order they were brought
        // up in. Anything else leaves the game's UI holding a pointer into
        // memory this plugin is about to release.
        foreach (var node in new List<object>(_live))
        {
            if (node is not OverlayShapeNode shape)
                continue;
            try
            {
                _controller?.RemoveNode(shape);
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"KamiToolKitOverlayPort: detaching a node during teardown failed: {ex.Message}");
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
                $"KamiToolKitOverlayPort: disposing the overlay controller failed: {ex.Message}");
        }
        _controller = null;

        _axisFont?.Dispose();
        _axisFont = null;
        _fontAtlas?.Dispose();
        _fontAtlas = null;

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
                $"KamiToolKitOverlayPort: disposing the node library failed: {ex.Message}");
        }
    }

    /// <summary>Brings the library and its controller up on first use, once.
    /// </summary>
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
                $"KamiToolKitOverlayPort: the node library is unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>One text request to one texture, through Dalamud's own
    /// SeString renderer — the game's Axis face with colour and edge, on
    /// the main thread the node update already runs on. Null on failure:
    /// the seat simply keeps what it last showed.</summary>
    private IDalamudTextureWrap? RenderText(TextRender request)
    {
        try
        {
            if (_axisFont == null)
            {
                _fontAtlas = _plugin.UiBuilder.CreateFontAtlas(
                    FontAtlasAutoRebuildMode.Async);
                _axisFont = _fontAtlas.NewGameFontHandle(
                    new GameFontStyle(GameFontFamily.Axis, 18f));
            }
            // The atlas builds asynchronously: until it lands the seat
            // simply keeps what it last showed and asks again next frame.
            if (!_axisFont.Available)
                return null;
            using var locked = _axisFont.Lock();
            return _textures.CreateTextureFromSeString(
                System.Text.Encoding.UTF8.GetBytes(request.Text),
                new SeStringDrawParams
                {
                    Font = locked.ImFont,
                    FontSize = request.FontSize,
                    WrapWidth = request.WrapWidth,
                    Color = PackColor(request.Color),
                    EdgeColor = request.Edge is { } edge
                        ? PackColor(edge)
                        : null,
                    Edge = request.Edge != null,
                    ForceEdgeColor = request.Edge != null,
                },
                "poser-overlay-text");
        }
        catch (Exception ex)
        {
            _log.Error(
                $"KamiToolKitOverlayPort: rendering overlay text failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>ImGui's RGBA byte order, which is what the SeString draw
    /// parameters take.</summary>
    private static uint PackColor(Vector4 color) =>
        ((uint)(Math.Clamp(color.W, 0f, 1f) * 255f) << 24)
        | ((uint)(Math.Clamp(color.Z, 0f, 1f) * 255f) << 16)
        | ((uint)(Math.Clamp(color.Y, 0f, 1f) * 255f) << 8)
        | (uint)(Math.Clamp(color.X, 0f, 1f) * 255f);

    /// <summary>One document onto one node: the shared frame first, then the
    /// status icon's path, which is the one field the node layer cannot
    /// resolve for itself.</summary>
    private void Write(OverlayShapeNode node, OverlayNodeState state)
    {
        if (state.Kind == OverlayNodeKind.Status)
            node.IconPath = IconPath(state.StatusIconId);
        node.State = state;
    }

    /// <summary>The icon's game path. Dalamud answers for ids the running
    /// client ships; anything else falls back to the sheet's own naming
    /// convention rather than leaving the square blank on a client whose
    /// lookup is merely unhappy.</summary>
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
            // Fall through to the convention below.
        }
        uint group = iconId / 1000 * 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ui/icon/{group:D6}/{iconId:D6}_hr1.tex");
    }

    private void TryDispose(OverlayShapeNode node)
    {
        // Cut the inbound edge first: a node being freed must not raise a move
        // against a token the service has already forgotten.
        node.Moved = null;
        try
        {
            node.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"KamiToolKitOverlayPort: freeing a node failed: {ex.Message}");
        }
    }
}
