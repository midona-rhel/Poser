using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Wraps <see cref="ImGui.BeginDragDropSource"/> / <see cref="ImGui.BeginDragDropTarget"/>
/// to carry managed payloads. ImGui's payload API is pointer-based; we register the
/// managed payload in a per-frame slot and pass an integer key through ImGui.
///
/// <para>Cross-window drops work for free — ImGui's drag-drop is context-global, and
/// Dalamud runs one ImGui context, so a source in window A and a target in window B
/// match when the payload type matches.</para>
/// </summary>
public static class DragDropAdapter
{
    private const string PayloadType = "NORV_DND";

    [System.ThreadStatic]
    private static Dictionary<int, object>? _payloads;
    [System.ThreadStatic]
    private static int _nextKey;
    [System.ThreadStatic]
    private static int _frameId;

    private static void EnsureFrameSlot()
    {
        int frame = ImGui.GetFrameCount();
        if (_frameId != frame)
        {
            _payloads?.Clear();
            _nextKey = 0;
            _frameId = frame;
        }
        _payloads ??= new Dictionary<int, object>();
    }

    /// <summary>
    /// If <paramref name="onDragStart"/> is non-null, opens a drag-drop source. The
    /// payload returned by <paramref name="onDragStart"/> is registered for one frame
    /// and forwarded via an integer key encoded as 4 payload bytes.
    /// </summary>
    public static void TrySource(Func<object>? onDragStart, string elementLabel)
    {
        if (onDragStart == null) return;
        if (!ImGui.BeginDragDropSource()) return;
        try
        {
            EnsureFrameSlot();
            var payload = onDragStart();
            int key = _nextKey++;
            _payloads![key] = payload;

            // Encode the 4-byte key as a little-endian span — Dalamud binding wants ReadOnlySpan<byte>.
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            MemoryMarshal.Write(bytes, in key);
            ImGui.SetDragDropPayload(PayloadType, bytes);

            ImGui.TextUnformatted(elementLabel);
        }
        finally
        {
            ImGui.EndDragDropSource();
        }
    }

    /// <summary>
    /// If <paramref name="onDrop"/> is non-null, opens a drop target. On a successful
    /// drop, looks up the registered payload and invokes the receiver. Returns true if
    /// a payload is currently being hovered with the right type AND <paramref name="canAccept"/>
    /// approves it — so the caller can paint a hover-feedback color.
    /// </summary>
    public static bool TryTarget(Action<object>? onDrop, Func<object, bool>? canAccept)
    {
        if (onDrop == null) return false;
        if (!ImGui.BeginDragDropTarget()) return false;
        try
        {
            unsafe
            {
                var peek = ImGui.AcceptDragDropPayload(PayloadType, ImGuiDragDropFlags.AcceptPeekOnly);
                if (peek.Handle == null) return false;

                int key = *(int*)peek.Data;
                if (_payloads == null || !_payloads.TryGetValue(key, out var payloadObj)) return false;
                if (canAccept != null && !canAccept(payloadObj)) return false;

                var accepted = ImGui.AcceptDragDropPayload(PayloadType);
                if (accepted.Handle != null && accepted.IsDelivery())
                    onDrop(payloadObj);

                return true;
            }
        }
        finally
        {
            ImGui.EndDragDropTarget();
        }
    }
}
