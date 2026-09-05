using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.UI;

public partial class MainWindow
{
    // Preserve the entity's existing capability gates and callbacks while
    // giving its secondary actions the same home across every context menu.
    private static List<Action?> MoveMoreActions(List<ContextMenuItem> items, List<Action?> actions)
    {
        var more = new List<ContextMenuItem>();
        var callbacks = new List<Action?>();
        for (int i = 0; i < items.Count;)
        {
            string label = items[i].Label;
            if (label.StartsWith("Save to file", StringComparison.Ordinal)
                || label == "Save to library"
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
        if (more.Count == 0)
            return callbacks;
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
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].IsSeparator && (i == 0 || i == items.Count - 1 || items[i - 1].IsSeparator))
            {
                items.RemoveAt(i);
                actions.RemoveAt(i);
            }
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("More", TablerIcon.Dots, submenuItems: more.ToArray()));
        actions.Add(null);
        if (lifetime.Count > 0)
        {
            items.Add(ContextMenuItem.Separator);
            actions.Add(null);
            items.AddRange(lifetime);
            actions.AddRange(lifetimeActions);
        }
        return callbacks;
    }

    private static List<Action?> MoveMoreActions(ref ContextMenuItem[] items, ref Action?[] actions)
    {
        var rows = items.ToList();
        var callbacks = actions.ToList();
        var more = MoveMoreActions(rows, callbacks);
        items = rows.ToArray();
        actions = callbacks.ToArray();
        return more;
    }

    private static void DrawMoreAction(IReadOnlyList<ContextMenuItem> items, List<Action?> more)
    {
        int clicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(out int parent);
        if (parent >= 0 && parent < items.Count && items[parent].Label == "More"
            && clicked >= 0 && clicked < more.Count)
            more[clicked]?.Invoke();
    }
}
