using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Identity;
using Poser.UI.Widgets;

namespace Poser.UI;

internal sealed partial class EntityContextMenus
{
    /// <summary>Puts an entity's menu together and draws it. Every row carries its
    /// own action and its place, so the order never depends on a row's text.
    /// With a <paramref name="target"/>, the actions every movable thing shares
    /// (Move to camera, Reset) are added too.</summary>
    private void DrawMenu(string menu, ref bool requested, List<ContextMenuItem> items, SelectionId? target = null)
    {
        if (target is { } id) AddCommonCommands(items, id);
        var rows = Arrange(items);
        if (requested)
        {
            requested = false;
            OpenContextMenu(menu, rows);
        }
        FloatingMenu.Refresh(menu, rows);
        int clicked = FloatingMenu.Draw(menu);
        if (clicked >= 0 && clicked < rows.Length)
            rows[clicked].OnInvoke?.Invoke();
        int child = FloatingMenu.ConsumeSubmenuClick(out int parent);
        if (parent >= 0 && parent < rows.Length && rows[parent].SubmenuItems is { } children
            && child >= 0 && child < children.Length)
            children[child].OnInvoke?.Invoke();
    }

    /// <summary>Sorts the rows by their place, with a line between groups. Saving
    /// and the rarer actions go into a More submenu, and the actions that remove
    /// the thing go last.</summary>
    private static ContextMenuItem[] Arrange(List<ContextMenuItem> items)
    {
        var rows = items.Where(item => !item.IsSeparator).Select(Flatten).ToList();
        var main = rows.Where(row => row.Place is not (MenuPlace.More or MenuPlace.Lifetime))
            .OrderBy(row => Group(row.Place)).ThenBy(row => (int)row.Place).ToList();
        var more = rows.Where(row => row.Place == MenuPlace.More).ToArray();
        var lifetime = rows.Where(row => row.Place == MenuPlace.Lifetime).ToList();

        var arranged = new List<ContextMenuItem>();
        int previous = -1;
        foreach (var row in main)
        {
            int group = Group(row.Place);
            if (previous >= 0 && previous != group) arranged.Add(ContextMenuItem.Separator);
            arranged.Add(row);
            previous = group;
        }
        if (more.Length > 0)
        {
            if (arranged.Count > 0) arranged.Add(ContextMenuItem.Separator);
            arranged.Add(more.Length == 1 ? more[0] : new ContextMenuItem("More", TablerIcon.Dots, submenuItems: more));
        }
        if (lifetime.Count > 0)
        {
            if (arranged.Count > 0) arranged.Add(ContextMenuItem.Separator);
            arranged.AddRange(lifetime);
        }
        return arranged.ToArray();
    }

    /// <summary>How the thing is shown comes first, then its own actions, then
    /// Rename and Duplicate, then the submenus.</summary>
    private static int Group(MenuPlace place) => place switch
    {
        >= MenuPlace.Handle and <= MenuPlace.Collision => 0,
        MenuPlace.Rename or MenuPlace.Duplicate => 2,
        >= MenuPlace.PlacementMenu and <= MenuPlace.TreeMenu => 3,
        _ => 1,
    };

    /// <summary>A submenu with a single row becomes that row, so a menu never
    /// opens a list of one.</summary>
    private static ContextMenuItem Flatten(ContextMenuItem item)
    {
        var children = item.SubmenuItems?.Where(child => !child.IsSeparator).ToArray();
        if (children is not { Length: 1 } || children[0].OnInvoke == null)
            return item;
        var child = children[0];
        string label = item.Place switch
        {
            MenuPlace.ResetMenu => "Reset " + char.ToLowerInvariant(child.Label[0]) + child.Label[1..],
            MenuPlace.CompanionMenu => child.Label + " companion",
            _ => child.Label,
        };
        return child with { Label = label, Disabled = item.Disabled || child.Disabled, Place = item.Place };
    }

    private void AddCommonCommands(List<ContextMenuItem> items, SelectionId id)
    {
        var reset = new List<ContextMenuItem>();
        bool movable = _placement.CanPlace(id);
        bool worldEntity = id.Actor != null || id.Light != null || id.Prop != null || id.WorldObject != null
            || id.Overlay is { } collider && _overlayControl.Read(collider)?.State.Collider != null;
        void AddReset(string label, Action action, string? help = null) => reset.Add(new(label, TablerIcon.Refresh,
            disabled: !movable, help: help) { OnInvoke = action });
        if (worldEntity)
        {
            items.Add(MoveToCameraItem([id], disabled: !movable));
            AddReset("Rotation", () => _notices.Report(_placement.ResetComponent(id, Domain.Transforms.TransformOperation.Rotate).Outcome, "Reset rotation"), "Zero rotation; keep position and scale");
            if (id.Light == null)
                AddReset("Scale", () => _notices.Report(_placement.ResetComponent(id, Domain.Transforms.TransformOperation.Scale).Outcome, "Reset scale"), "Unit scale; keep position and rotation");
        }
        if (id.Actor is { } actor)
        {
            AddReset("Placement overrides", () => _notices.Report(_transforms.ClearActorOverrides([TransformTargetId.ForActor(actor)]).Outcome, "Reset placement overrides"), "Release this actor's authored world placement");
            reset.Add(ContextMenuItem.Separator);
            foreach (var region in Enum.GetValues<Domain.Posing.PoseRegion>())
            {
                var captured = region;
                reset.Add(new(region == Domain.Posing.PoseRegion.All ? "Pose" : region.ToString(), TablerIcon.Refresh,
                    disabled: _scene.Snapshot.FindActor(actor)?.CharacterSkeleton == null, disruptive: true)
                { OnInvoke = () => _notices.Report(_cleanPose.Reset(actor, captured).Outcome, captured == Domain.Posing.PoseRegion.All ? "Reset pose" : "Reset " + captured.ToString().ToLowerInvariant()) });
            }
        }
        else if (id.Overlay is { } overlay && !worldEntity)
            reset.Add(new("Size and opacity", TablerIcon.Refresh)
            { OnInvoke = () => _notices.Report(_overlayControl.Set(overlay, Application.Presentation.OverlayProperties.Size, (1f, 1f)), "Reset size and opacity") });
        if (reset.Count == 0 || items.Any(item => item.Place == MenuPlace.ResetMenu)) return;
        items.Add(Submenu("Reset", TablerIcon.Refresh, MenuPlace.ResetMenu, reset.ToArray()));
    }
}
