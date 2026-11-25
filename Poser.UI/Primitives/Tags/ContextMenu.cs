using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>One floating-menu entry.</summary>
public record struct ContextMenuItem
{
    public string Label;
    public TablerIcon Icon;
    public string? Shortcut;
    public bool Danger;
    public bool Disabled;
    public bool IsSeparator;
    public ContextMenuItem[]? SubmenuItems;
    // Optional UI command binding. Legacy callers may still consume indices;
    // composed menus keep a child callback attached to its visible row.
    public Action? OnInvoke;

    /// <summary>Explanatory hover help for the row — the same card every
    /// control's <c>help</c> shows. A disabled row especially needs it to explain
    /// disabled row explaining why it is unavailable, which has no live
    /// item to hover, so the menu registers it geometrically.</summary>
    public string? Help;

    /// <summary>A toggle row: clicking it fires but leaves the menu open,
    /// so several can be set in one visit. Pair it with a refresh of the
    /// items so the row shows its new state.</summary>
    public bool KeepOpen;

    /// <summary>A verb that breaks animation state: the row wears the
    /// Disruptive colour the way a destructive row wears Danger.</summary>
    public bool Disruptive;

    /// <summary>Where the row goes in an entity's right-click menu.</summary>
    public MenuPlace Place;

    public ContextMenuItem(
        string label,
        TablerIcon icon = TablerIcon.Circle,
        string? shortcut = null,
        bool danger = false,
        bool disabled = false,
        string? help = null,
        ContextMenuItem[]? submenuItems = null,
        bool keepOpen = false,
        bool disruptive = false)
    {
        Label = label;
        Icon = icon;
        Shortcut = shortcut;
        Danger = danger;
        Disruptive = disruptive;
        Disabled = disabled;
        Help = help;
        SubmenuItems = submenuItems;
        KeepOpen = keepOpen;
        IsSeparator = false;
    }

    public static ContextMenuItem Separator => new() { IsSeparator = true, Label = string.Empty };
}

/// <summary>Where a row goes in an entity's right-click menu. Menus are sorted
/// by this, with a line between groups: first how the thing is shown, then
/// its own actions, then Rename and Duplicate, then the submenus, then More,
/// and last the actions that remove it.</summary>
public enum MenuPlace
{
    /// <summary>An action that belongs to this kind of thing only.</summary>
    Own,
    Handle, Framing, View, Visibility, Playback, Lock, Collision,
    Rename, Duplicate,
    PlacementMenu, PlaybackMenu, SelectMenu, VisibilityMenu, PoseMenu, ResetMenu, CompanionMenu, BonePresetsMenu, TreeMenu,
    /// <summary>Goes into the More submenu: saving, and the rarer actions.</summary>
    More,
    /// <summary>Destroy, Remove or Release, at the very bottom.</summary>
    Lifetime,
}
