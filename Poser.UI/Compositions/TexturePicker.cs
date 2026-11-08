using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>Result of probing a candidate texture id.</summary>
public enum TextureProbe
{
    /// <summary>The game has no such texture: the id is dropped for good.
    /// </summary>
    Missing,

    /// <summary>The texture exists but its wrap is not ready this frame, so
    /// the id is asked again. A game texture is loaded asynchronously, which
    /// makes this the usual answer the first time an id is asked.
    /// </summary>
    Pending,

    /// <summary>Resolved: the handle is this frame's and must not be kept.
    /// </summary>
    Ready,
}

/// <summary>Resolves a candidate texture id to a frame-local ImGui handle
/// and its pixel dimensions. The caller owns game paths and texture access.
///
/// <para>The size is what tells a picture from an animation atlas, which the
/// tile must sample differently; an unresolved probe returns
/// <see cref="Vector2.Zero"/> and is drawn whole.</para></summary>
public delegate TextureProbe TexturePreview(
    uint id, out nint handle, out Vector2 pixels);
