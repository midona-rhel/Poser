using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum SidebarExpander { None, Collapsed, Open }

/// <summary>The result of one tree-row gesture.</summary>
public enum TreeRowAction { None, Selected, Expander, Context, Drag }

/// <summary>Visual and interaction state for one tree row.</summary>
public record struct TreeRowProps
{
    /// <summary>The fallback icon when no texture is available.</summary>
    public TablerIcon? Icon;

    /// <summary>A registered icon name.</summary>
    public string? IconName;

    /// <summary>A caller-owned texture resolved for this frame.</summary>
    public IDalamudTextureWrap? IconTexture;

    /// <summary>Removes the icon and its spacing.</summary>
    public bool HideIcon;

    /// <summary>An optional label size override.</summary>
    public float? LabelSize;

    /// <summary>Centers the label inside its available span.</summary>
    public bool CenterLabel;

    /// <summary>Right-aligned mono readout (counts, "you", "spawned").</summary>
    public string? Badge;

    /// <summary>0 is a root row; each level costs one indent.</summary>
    public int Depth;

    /// <summary>Ancestor depths whose sibling lines continue.</summary>
    public uint Trunks;

    /// <summary>Last child of its parent — the branch is an L, not a T.
    /// </summary>
    public bool IsLastChild;

    /// <summary>Hides guides without changing row geometry.</summary>
    public bool HideGuides;

    /// <summary>The disclosure state.</summary>
    public SidebarExpander Expander;

    /// <summary>Shows an inert disclosure without changing layout.</summary>
    public bool ExpanderDisabled;

    public bool Selected;

    /// <summary>Drag-hover: the accent fill over its own hairline.</summary>
    public bool DropTarget;

    /// <summary>Whether the row can be picked up and dragged — set by the
    /// host for the rows whose order or grouping is the user's to move.
    /// </summary>
    public bool Draggable;

    /// <summary>While a drag is live the plain hover fill goes silent —
    /// only the drop indicators speak.</summary>
    public bool SuppressHover;

    /// <summary>The CURRENT one: the game's target actor and the live
    /// camera wear their row mark in the ACCENT at full strength, so
    /// which is which reads at a glance without selecting anything. (A
    /// full-row outline was tried 2026-08-30 and replaced by this.)</summary>
    public bool Marked;

    /// <summary>Right padding reserved for the scroll gutter.</summary>
    public float TrailingInset;

    /// <summary>Action slots reserved after the label.</summary>
    public int ActionSlots;

    /// <summary>The action square's logical side; 0 takes the shell's switch
    /// size.</summary>
    public float ActionSide;
}
