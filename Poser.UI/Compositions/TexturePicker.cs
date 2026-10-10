using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>What one candidate texture id answered when it was probed.</summary>
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

/// <summary>Resolves one candidate texture id to a frame-local ImGui handle
/// and the pixel size of the image behind it. Stated by the caller, exactly as
/// <see cref="PickerOptions{T}.Texture"/> is — game paths and the texture
/// service stay outside the widgets.
///
/// <para>The size is what tells a picture from an animation atlas, which the
/// tile must sample differently; an unresolved probe answers
/// <see cref="Vector2.Zero"/> and is drawn whole.</para></summary>
public delegate TextureProbe TexturePreview(
    uint id, out nint handle, out Vector2 pixels);
