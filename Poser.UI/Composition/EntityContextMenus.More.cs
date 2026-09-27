using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Identity;

namespace Poser.UI;

internal sealed partial class EntityContextMenus
{
    private void AddHandleAction(List<ContextMenuItem> items, List<Action?> actions, SelectionId target)
    {
        bool shown = _overlayPresentation.IsHandleShown(target);
        items.Insert(0, new ContextMenuItem(shown ? "Hide handle" : "Show handle",
            shown ? TablerIcon.EyeOff : TablerIcon.Eye, keepOpen: true,
            help: "Only the overlay handle; does not hide the entity"));
        actions.Insert(0, () => _overlayPresentation.ToggleHandle(target));
    }

    private void AddHandleAction(ref ContextMenuItem[] items, ref Action?[] actions, SelectionId target)
    {
        var rows = items.ToList();
        var callbacks = actions.ToList();
        AddHandleAction(rows, callbacks, target);
        items = rows.ToArray();
        actions = callbacks.ToArray();
    }

    // Preserve the entity's existing capability gates and callbacks while
    // giving its secondary actions the same home across every context menu.
    private List<Action?> MoveMoreActions(List<ContextMenuItem> items, List<Action?> actions, SelectionId? target = null)
    {
        if (target is { } id) AddCommonCommands(items, actions, id);
        FlattenSingleActions(items, actions);
        var more = new List<ContextMenuItem>();
        var callbacks = new List<Action?>();
        for (int i = 0; i < items.Count;)
        {
            string label = items[i].Label;
            if (label.StartsWith("Save to file", StringComparison.Ordinal)
                || label == "Save to library"
                || label is "Create collider" or "Export Idle Pose…"
                || label.StartsWith("Destroy all", StringComparison.Ordinal))
            {
                more.Add(items[i]);
                callbacks.Add(actions[i]);
                items.RemoveAt(i);
                actions.RemoveAt(i);
            }
            else
                i++;
        }
        var lifetime = new List<ContextMenuItem>();
        var lifetimeActions = new List<Action?>();
        for (int i = 0; i < items.Count;)
        {
            if (items[i].Label is "Destroy" or "Delete" or "Remove" or "Release")
            {
                lifetime.Add(items[i]);
                lifetimeActions.Add(actions[i]);
                items.RemoveAt(i);
                actions.RemoveAt(i);
            }
            else
                i++;
        }
        // One composition rule for actor, camera, light, scenery, prop, collider
        // and group menus; keep callbacks paired while arranging their sections.
        var rows = items.Select((item, i) => (Item: item, Action: actions[i]))
            .Where(row => !row.Item.IsSeparator).OrderBy(row => Section(row.Item.Label))
            .ThenBy(row => Rank(row.Item.Label)).ToArray();
        items.Clear(); actions.Clear();
        int previous = -1;
        foreach (var row in rows)
        {
            int section = Section(row.Item.Label);
            if (previous >= 0 && previous != section) { items.Add(ContextMenuItem.Separator); actions.Add(null); }
            items.Add(row.Item); actions.Add(row.Action); previous = section;
        }
        if (more.Count > 0)
        {
            if (items.Count > 0) { items.Add(ContextMenuItem.Separator); actions.Add(null); }
            if (more.Count == 1) { items.Add(more[0]); actions.Add(callbacks[0]); }
            else { items.Add(new ContextMenuItem("More", TablerIcon.Dots, submenuItems: more.ToArray())); actions.Add(null); }
        }
        if (lifetime.Count > 0)
        {
            items.Add(ContextMenuItem.Separator);
            actions.Add(null);
            items.AddRange(lifetime);
            actions.AddRange(lifetimeActions);
        }
        return callbacks;
    }

    private static int Section(string label) => label switch
    {
        "Show handle" or "Hide handle" or "Show" or "Hide" or "Switch on" or "Switch off" or "Show in overlay" or "Hide from overlay"
            or "Lock" or "Unlock" or "Lock transform" or "Unlock transform"
            or "Enable collision" or "Disable collision" or "Play" or "Pause"
            or "Center camera on actor" or "Set game target" or "Look through"
            or "Return to main camera" or "Look at tracked actor" => 0,
        "Rename" or "Duplicate" => 2,
        "Reset" or "Tree" or "Bone presets" or "Pose" or "Companion" or "Attachment" or "Placement" or "Playback" or "Select" or "Visibility" or "Actor pose" or "Actor bone presets" => 3,
        _ => 1,
    };

    private static int Rank(string label) => label switch
    {
        "Show handle" or "Hide handle" => 0,
        "Center camera on actor" or "Look at tracked actor" => 1,
        "Set game target" or "Look through" or "Return to main camera" => 2,
        "Show" or "Hide" or "Switch on" or "Switch off" or "Show in overlay" or "Hide from overlay" => 3,
        "Play" or "Pause" => 4,
        "Lock" or "Unlock" or "Lock transform" or "Unlock transform" => 5,
        "Enable collision" or "Disable collision" => 6,
        "Rename" => 0, "Duplicate" => 1,
        "Placement" => 10, "Playback" => 20, "Select" => 30, "Visibility" => 40,
        "Pose" or "Actor pose" => 50, "Reset" => 60, "Companion" or "Attachment" => 70,
        "Bone presets" or "Actor bone presets" => 80, "Tree" => 90,
        _ => 0,
    };

    private List<Action?> MoveMoreActions(ref ContextMenuItem[] items, ref Action?[] actions, SelectionId? target = null)
    {
        var rows = items.ToList();
        var callbacks = actions.ToList();
        var more = MoveMoreActions(rows, callbacks, target);
        items = rows.ToArray();
        actions = callbacks.ToArray();
        return more;
    }

    private static void DrawMoreAction(IReadOnlyList<ContextMenuItem> items, List<Action?> more)
    {
        int clicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(out int parent);
        if (InvokeComposedAction(items, parent, clicked)) return;
        if (parent >= 0 && parent < items.Count && items[parent].Label == "More"
            && clicked >= 0 && clicked < more.Count)
            more[clicked]?.Invoke();
    }

    private static bool InvokeComposedAction(IReadOnlyList<ContextMenuItem> items, int parent, int child)
    {
        if (parent < 0 || parent >= items.Count || items[parent].SubmenuItems is not { } children
            || child < 0 || child >= children.Length || children[child].OnInvoke is not { } invoke) return false;
        invoke();
        return true;
    }

    private static ContextMenuItem[] BindSubmenu(ContextMenuItem[] items, IReadOnlyList<Action?> actions) =>
        items.Select((item, index) => item with { OnInvoke = actions[index] }).ToArray();

    private void DrawComposedMenu(string menu, ref bool requested, ContextMenuItem[] items)
    {
        var actions = items.Select(item => item.OnInvoke).ToArray();
        var more = MoveMoreActions(ref items, ref actions);
        if (requested) { requested = false; OpenContextMenu(menu, items); }
        Crystarium.FloatingMenu.Refresh(menu, items);
        int clicked = Crystarium.FloatingMenu.Draw(menu);
        if (clicked >= 0 && clicked < actions.Length) actions[clicked]?.Invoke();
        DrawMoreAction(items, more);
    }

    private static void FlattenSingleActions(List<ContextMenuItem> items, List<Action?> actions)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var children = items[i].SubmenuItems?.Where(child => !child.IsSeparator).ToArray();
            if (children is not { Length: 1 } || children[0].OnInvoke is not { } action) continue;
            var child = children[0];
            string label = items[i].Label switch
            {
                "Reset" => "Reset " + char.ToLowerInvariant(child.Label[0]) + child.Label[1..],
                "Companion" => child.Label + " companion",
                _ => child.Label,
            };
            items[i] = child with { Label = label, Disabled = items[i].Disabled || child.Disabled };
            actions[i] = action;
        }
    }

    private void AddCommonCommands(List<ContextMenuItem> items, List<Action?> actions, SelectionId id)
    {
        var reset = new List<ContextMenuItem>();
        bool movable = _placement.CanPlace(id);
        bool worldEntity = id.Actor != null || id.Light != null || id.Prop != null || id.WorldObject != null
            || id.Overlay is { } collider && _overlayControl.Read(collider)?.State.Collider != null;
        void AddReset(string label, Action action, string? help = null) => reset.Add(new(label, TablerIcon.Refresh,
            disabled: !movable, help: help) { OnInvoke = action });
        if (worldEntity)
        {
            if (!items.Any(item => item.Label == "Move to camera"))
            {
                items.Add(new("Move to camera", TablerIcon.Camera, disabled: !movable));
                actions.Add(() => ReportPlacement(_placement.MoveToCamera([id])));
            }
            AddReset("Rotation", () => ReportPlacement(_placement.ResetComponent(id, Domain.Transforms.TransformOperation.Rotate)), "Zero rotation; keep position and scale");
            if (id.Light == null)
                AddReset("Scale", () => ReportPlacement(_placement.ResetComponent(id, Domain.Transforms.TransformOperation.Scale)), "Unit scale; keep position and rotation");
        }
        if (id.Actor is { } actor)
        {
            AddReset("Placement overrides", () => ReportPlacement(_transforms.ClearActorOverrides([TransformTargetId.ForActor(actor)])), "Release this actor's authored world placement");
            reset.Add(ContextMenuItem.Separator);
            foreach (var region in Enum.GetValues<Domain.Posing.PoseRegion>())
            {
                var captured = region;
                reset.Add(new(region == Domain.Posing.PoseRegion.All ? "Pose" : region.ToString(), TablerIcon.Refresh,
                    disabled: _scene.Snapshot.FindActor(actor)?.CharacterSkeleton == null, disruptive: true)
                { OnInvoke = () => { var result = _cleanPose.Reset(actor, captured); if (!result.Success) _notices.Failed("Reset pose", result.Detail ?? "The pose could not be reset."); } });
            }
        }
        else if (id.Overlay is { } overlay && !worldEntity)
            reset.Add(new("Size and opacity", TablerIcon.Refresh)
            { OnInvoke = () => { var result = _overlayControl.ResetSize(overlay); if (!result.Success) _notices.Failed("Reset", result.Detail ?? "The overlay could not be reset."); } });
        if (reset.Count == 0 || items.Any(item => item.Label == "Reset")) return;
        items.Add(new("Reset", TablerIcon.Refresh, submenuItems: reset.ToArray())); actions.Add(null);
    }

    private void ReportPlacement(Domain.Transforms.GestureResult result)
    {
        if (!result.Success) _notices.Failed("Placement", result.Detail ?? "The transform could not be changed.");
    }
}
