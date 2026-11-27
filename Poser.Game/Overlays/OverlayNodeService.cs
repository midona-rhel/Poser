using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Domain.Presentation;
using Poser.Application.Events;
using Poser.Game.Core;
using Poser.Game.Services;

namespace Poser.Game.Overlays;

/// <summary>
/// One overlay: its stored values and the game UI node that draws it (an IK
/// collider has no node). Each setter stores the value, then has the node drawn
/// with it. A removed overlay keeps its values but has no node left to draw.
/// </summary>
public sealed class OverlayNodeHandle : IOverlayNode
{
    private readonly OverlayNodeService _owner;
    private object? _node;
    private OverlayNodeState _state;

    internal OverlayNodeHandle(
        OverlayNodeService owner, int id, object node, OverlayNodeState state)
    {
        _owner = owner;
        Id = id;
        _node = node;
        _state = state;
    }

    public int Id { get; }

    public OverlayNodeKind Kind => _state.Kind;

    public bool IsValid => _node != null;

    internal object? Node => _node;

    /// <summary>Stores the values, kept within their limits, and redraws the node.</summary>
    public OverlayNodeState State
    {
        get => _state;
        set
        {
            var next = value.Normalized();
            _state = next;
            if (_node is { } node)
                _owner.ApplyToNode(node, next);
        }
    }

    public string Name
    {
        get => _state.Name;
        set => State = _state with { Name = value };
    }

    public Vector2 Position
    {
        get => _state.Position;
        set => State = _state with { Position = value };
    }

    public float Scale
    {
        get => _state.Scale;
        set => State = _state with { Scale = value };
    }

    public float Alpha
    {
        get => _state.Alpha;
        set => State = _state with { Alpha = value };
    }

    public bool Visible
    {
        get => _state.Visible;
        set => State = _state with { Visible = value };
    }

    public bool Draggable
    {
        get => _state.Draggable;
        set => State = _state with { Draggable = value };
    }

    public string Text
    {
        get => _state.Text;
        set => State = _state with { Text = value };
    }

    public string Speaker
    {
        get => _state.Speaker;
        set => State = _state with { Speaker = value };
    }

    public uint FontSize
    {
        get => _state.FontSize;
        set => State = _state with { FontSize = value };
    }

    public TalkBackground TalkBackground
    {
        get => _state.TalkBackground;
        set => State = _state with { TalkBackground = value };
    }

    public TalkCursor TalkCursor
    {
        get => _state.TalkCursor;
        set => State = _state with { TalkCursor = value };
    }

    public BalloonChannel BalloonChannel
    {
        get => _state.BalloonChannel;
        set => State = _state with { BalloonChannel = value };
    }

    public BalloonGradient BalloonGradient
    {
        get => _state.BalloonGradient;
        set => State = _state with { BalloonGradient = value };
    }

    public bool ArrowVisible
    {
        get => _state.ArrowVisible;
        set => State = _state with { ArrowVisible = value };
    }

    public float ArrowX
    {
        get => _state.ArrowX;
        set => State = _state with { ArrowX = value };
    }

    public StatusKind StatusKind
    {
        get => _state.StatusKind;
        set => State = _state with { StatusKind = value };
    }

    public uint StatusIconId
    {
        get => _state.StatusIconId;
        set => State = _state with { StatusIconId = value };
    }

    public void Destroy() => _owner.Destroy(this);

    /// <summary>The player dragged the node and the game has already moved it,
    /// so only the stored position is updated.</summary>
    internal void AdoptDraggedPosition(Vector2 position) =>
        _state = (_state with { Position = position }).Normalized();

    /// <summary>Forgets the node once the service has removed it.</summary>
    internal void Invalidate() => _node = null;
}

/// <summary>
/// The list of overlays: dialog boxes, speech balloons, status lines and IK
/// colliders. Creates and removes them, and removes all of them when the scene
/// is cleared, when the player leaves GPose and when Poser unloads. New overlays
/// are named after their kind with the next free number.
/// </summary>
public sealed class OverlayNodeService : IDisposable, IOverlayNodeService
{
    private sealed class ColliderToken;
    private readonly IOverlayNodePort _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly List<OverlayNodeHandle> _nodes = new();

    private int _nextId;
    private bool _disposed;

    public OverlayNodeService(IOverlayNodePort port, IEventBus events, IPluginLog log)
    {
        _port = port;
        _events = events;
        _log = log;
        _port.Moved = OnNodeMoved;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>The player dragged an overlay on screen; the game has already
    /// moved it. Gives the handle and its position before the drag.
    /// <see cref="OverlayControl"/> records it as an undo step, like every
    /// other overlay edit.</summary>
    public event Action<OverlayNodeHandle, Vector2>? Dragged;

#if DEBUG
    /// <summary>Debug bridge only: does what a drag the player makes on screen
    /// does, ending at <paramref name="position"/>. Like the game, it moves the
    /// node on screen first, then reports the drag. The game's own detection
    /// of the mouse drag is not involved.</summary>
    public void DebugDrag(OverlayNodeHandle handle, Vector2 position)
    {
        var node = handle.Node!;
        ApplyToNode(node, handle.State with { Position = position });
        OnNodeMoved(node, position);
    }
#endif

    /// <summary>A drag landed: finds whose node it was and updates that
    /// overlay's stored position. A node no overlay holds any more is ignored.</summary>
    private void OnNodeMoved(object node, Vector2 position)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (!ReferenceEquals(_nodes[i].Node, node))
                continue;
            var handle = _nodes[i];
            var before = handle.Position;
            handle.AdoptDraggedPosition(position);
            Dragged?.Invoke(handle, before);
            return;
        }
    }

    /// <summary>The current overlays. This is the service's own list, so a
    /// caller that removes overlays while going through it must copy it first.</summary>
    public IReadOnlyList<OverlayNodeHandle> Nodes => _nodes;

    public int Count => _nodes.Count;

    /// <summary>False after unloading or when the overlay library could not
    /// start. The library starts on the first create, so true does not promise
    /// that the next create works.</summary>
    public bool IsAvailable => !_disposed && _port.IsAvailable;

    /// <summary>Creates an overlay of <paramref name="kind"/> with that kind's
    /// starting values. Null when the game refused it.</summary>
    public OverlayNodeHandle? Create(OverlayNodeKind kind) =>
        Create(DefaultState(kind));

    /// <summary>
    /// Creates an overlay with the given values; undo and scene loading use
    /// this. Without a name it is named like a new overlay.
    /// </summary>
    public OverlayNodeHandle? Create(OverlayNodeState state)
    {
        if (_disposed)
            return null;
        var document = state.Normalized();
        if (document.Name.Length == 0)
            document = document with { Name = NextName(document.Kind) };
        try
        {
            var node = document.Kind == OverlayNodeKind.Collider
                ? new ColliderToken() : _port.Create(document);
            if (node == null)
            {
                _log.Warning(
                    $"OverlayNodeService: the game refused a {document.Kind} node.");
                return null;
            }
            var handle = new OverlayNodeHandle(
                this, ++_nextId, node, document);
            _nodes.Add(handle);
            _events.Publish(new OverlayNodeListChangedEvent());
            return handle;
        }
        catch (Exception ex)
        {
            _log.Error($"OverlayNodeService: creating a node failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Removes one overlay. Removing one that is already gone does nothing.</summary>
    public void Destroy(OverlayNodeHandle? handle)
    {
        if (handle == null)
            return;
        if (_nodes.Remove(handle))
            _events.Publish(new OverlayNodeListChangedEvent());
        DestroyNative(handle);
    }

    /// <summary>Removes every overlay: on scene clear, on leaving GPose and on unload.</summary>
    public void DestroyAll()
    {
        if (_nodes.Count == 0)
            return;
        _log.Information($"[GPoseLifetime] overlay-cleanup-before total={_nodes.Count} colliders={_nodes.Count(n => n.Kind == OverlayNodeKind.Collider)}");
        for (int i = 0; i < _nodes.Count; i++)
            DestroyNative(_nodes[i]);
        _nodes.Clear();
        _log.Information("[GPoseLifetime] overlay-cleanup-complete");
        _events.Publish(new OverlayNodeListChangedEvent());
    }

    /// <summary>Redraws a node with new values. The values are already stored,
    /// so when the overlay library throws, the failure is logged and the node
    /// stays as it was last drawn.</summary>
    internal void ApplyToNode(object node, OverlayNodeState state)
    {
        if (_disposed || node is ColliderToken)
            return;
        try
        {
            _port.Apply(node, state);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"OverlayNodeService: re-stating a node failed: {ex.Message}");
        }
    }

    private void DestroyNative(OverlayNodeHandle handle)
    {
        if (handle.Node is not { } node)
            return;
        // The port catches and logs its own failures.
        if (node is not ColliderToken)
            _port.Destroy(node);
        handle.Invalidate();
    }

    private string NextName(OverlayNodeKind kind)
    {
        return Poser.Domain.Scene.EntityNames.Next(KindName(kind), _nodes.Select(x => x.Name));
    }

    private static string KindName(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Collider => "IK collider",
        OverlayNodeKind.Balloon => "Balloon",
        OverlayNodeKind.Status => "Status",
        _ => "Dialog",
    };

    /// <summary>The starting values of a new overlay of each kind, the same as
    /// Ktisis uses, so a new overlay looks like the game's own.</summary>
    public static OverlayNodeState DefaultState(OverlayNodeKind kind) =>
        kind switch
        {
            OverlayNodeKind.Collider => new OverlayNodeState
            {
                Kind = OverlayNodeKind.Collider,
                Collider = new(),
                Alpha = .2f,
            },
            OverlayNodeKind.Balloon => new OverlayNodeState
            {
                Kind = OverlayNodeKind.Balloon,
                Position = new Vector2(500f, 500f),
                Text = "New dialog...",
                FontSize = 12,
                ArrowVisible = true,
                ArrowX = OverlayNodeLimits.MaxArrowX,
            },
            OverlayNodeKind.Status => new OverlayNodeState
            {
                Kind = OverlayNodeKind.Status,
                Position = new Vector2(450f, 450f),
                Text = "New Status",
                StatusKind = StatusKind.Buff,
                StatusIconId = DefaultStatusIconId,
            },
            _ => new OverlayNodeState
            {
                Kind = OverlayNodeKind.Talk,
                Position = new Vector2(600f, 600f),
                Speaker = "Speaker",
                Text = "New dialog...",
                FontSize = 14,
                TalkBackground = TalkBackground.Basic,
                TalkCursor = TalkCursor.Pin,
            },
        };

    /// <summary>The first ordinary status icon, so a new status line shows a
    /// picture. Ktisis uses the same icon.</summary>
    public const uint DefaultStatusIconId = 213001;

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            DestroyAll();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _port.Moved = null;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        DestroyAll();
        // The port also frees any node the list above did not know about.
        _port.Dispose();
    }
}
