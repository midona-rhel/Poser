using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Presentation;
using Poser.Services;

namespace Poser.Game.Overlays;

/// <summary>
/// One spawned overlay node: a stable id and a live document over a native UI
/// subtree. Every write goes through to the node — the handle keeps the
/// document only so a read costs nothing and a removal has something to
/// capture — and a handle whose node has been destroyed reads its last
/// document and writes nothing, exactly as a destroyed prop's handle does.
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

    /// <summary>The complete document. Assigning one re-states the whole node;
    /// it is normalized first, so nothing out of range ever reaches the game.
    /// </summary>
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

    /// <summary>The pointer dragged this node and the game has ALREADY moved
    /// it: the document catches up without writing anything back down. Called
    /// by the service, which hears it from the port.</summary>
    internal void AdoptDraggedPosition(Vector2 position) =>
        _state = (_state with { Position = position }).Normalized();

    /// <summary>The node behind this handle is gone. Called by the service
    /// AFTER the port has freed it, and never by anything else.</summary>
    internal void Invalidate() => _node = null;
}

/// <summary>
/// Owns every overlay node Poser has put on the screen, and — the only reason
/// this class exists rather than the port alone — owns their DEATH.
///
/// <para>A native UI node outlives its plugin unless something takes it back,
/// and the game will happily draw a subtree whose managed owner has been
/// collected. So the set is torn down at THREE edges, and the three are not
/// alternatives: GPose exit (the session the nodes were staged for is over),
/// an explicit scene clear (<see cref="DestroyAll"/>, the user's own act), and
/// <see cref="Dispose"/> (plugin unload, which the DI container runs). Each is
/// idempotent and each leaves the port holding nothing, so any two of them
/// firing in either order is still correct.</para>
///
/// <para>Names never repeat within a session — a destroyed "Dialog 2" does not
/// hand its name back — for the same reason a prop's does not: an undo that
/// restores a node must not collide with one spawned since.</para>
/// </summary>
public sealed class OverlayNodeService : IDisposable
{
    private readonly IOverlayNodePort _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly List<OverlayNodeHandle> _nodes = new();

    private int _nextId;
    private bool _disposed;

    public OverlayNodeService(
        IOverlayNodePort port, IEventBus events, IPluginLog log)
    {
        _port = port;
        _events = events;
        _log = log;
        _port.Moved = OnNodeMoved;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>A drag landed: find whose node it was and let that document
    /// catch up. Reference identity, like everything else that speaks in
    /// tokens; a token nobody still holds is simply ignored.</summary>
    private void OnNodeMoved(object node, Vector2 position)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (!ReferenceEquals(_nodes[i].Node, node))
                continue;
            _nodes[i].AdoptDraggedPosition(position);
            return;
        }
    }

    /// <summary>The live handle list. It is the service's own list, so a
    /// caller that destroys while reading must work off a snapshot.</summary>
    public IReadOnlyList<OverlayNodeHandle> Nodes => _nodes;

    public int Count => _nodes.Count;

    /// <summary>Whether the game's UI can host a node right now. A false here
    /// is what makes the create affordances read as unavailable instead of
    /// doing nothing.</summary>
    public bool IsAvailable => !_disposed && _port.IsAvailable;

    /// <summary>Creates one node of the stated kind, named for the user, at
    /// the kind's own default state. Null when the game refused it.</summary>
    public OverlayNodeHandle? Create(OverlayNodeKind kind) =>
        Create(DefaultState(kind));

    /// <summary>
    /// Creates one node from a complete document — the restore path, used by
    /// undo and by a scene load. An unnamed document is named as a fresh
    /// create would be.
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
            var node = _port.Create(document);
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

    /// <summary>Destroys one node and forgets it. Destroying a handle that has
    /// already gone is a no-op.</summary>
    public void Destroy(OverlayNodeHandle? handle)
    {
        if (handle == null)
            return;
        if (_nodes.Remove(handle))
            _events.Publish(new OverlayNodeListChangedEvent());
        DestroyNative(handle);
    }

    /// <summary>Takes back every node. The scene-clear edge, and the shared
    /// body of the GPose-exit and unload edges.</summary>
    public void DestroyAll()
    {
        if (_nodes.Count == 0)
            return;
        for (int i = 0; i < _nodes.Count; i++)
            DestroyNative(_nodes[i]);
        _nodes.Clear();
        _events.Publish(new OverlayNodeListChangedEvent());
    }

    /// <summary>The write-through half of <see cref="OverlayNodeHandle"/>. A
    /// port that throws must not take the handle's document with it — the
    /// document is already committed by then — so the failure is logged and
    /// the node is left as the game last drew it.</summary>
    internal void ApplyToNode(object node, OverlayNodeState state)
    {
        if (_disposed)
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
        try
        {
            _port.Destroy(node);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"OverlayNodeService: destroying a node failed: {ex.Message}");
        }
        finally
        {
            // The handle is invalidated even when the port threw: a token the
            // port may have half-freed must never be handed back to it.
            handle.Invalidate();
        }
    }

    private string NextName(OverlayNodeKind kind)
    {
        int ordinal = 1;
        foreach (var node in _nodes)
            if (node.Kind == kind)
                ordinal++;
        return KindName(kind) + " "
            + ordinal.ToString(CultureInfo.InvariantCulture);
    }

    private static string KindName(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Balloon => "Balloon",
        OverlayNodeKind.Status => "Status",
        _ => "Dialog",
    };

    /// <summary>What a freshly created node of each kind says, before the user
    /// has said anything. Mirrors Ktisis's own defaults so a node lands looking
    /// like the game's, not like an empty box.</summary>
    public static OverlayNodeState DefaultState(OverlayNodeKind kind) =>
        kind switch
        {
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

    /// <summary>The first ordinary status icon, so a fresh status line has a
    /// picture rather than a blank square (Ktisis
    /// <c>Scene/Entities/Utility/StatusOverlay.cs:19</c> names the same one by
    /// its path).</summary>
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
        // The port's own dispose is the last line of defence: it frees
        // anything the list above never knew about.
        _port.Dispose();
    }
}
