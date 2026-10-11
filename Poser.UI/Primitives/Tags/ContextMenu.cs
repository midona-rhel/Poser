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
    /// control's <c>help</c> shows. The one row shape that NEEDS it is a
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
