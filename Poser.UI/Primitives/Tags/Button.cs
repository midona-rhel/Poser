using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// The Picto action-button family (actionButton.module.css): Secondary
/// is <c>.btn</c>, Primary composes <c>.btnPrimary</c>, Danger composes
/// <c>.btnDanger</c>. There is no separate React component — the API is
/// native button behavior plus these composed classes, so the variant
/// is typed rather than a pile of booleans.
/// </summary>
public enum ButtonVariant
{
    Secondary,
    Primary,
    Danger,
    /// <summary>A verb that breaks animation state. Painted like Danger from
    /// the theme's one Disruptive token.</summary>
    Disruptive,
}
