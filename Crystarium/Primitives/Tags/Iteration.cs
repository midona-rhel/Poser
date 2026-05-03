using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Iterate items with stable ImGui IDs auto-pushed.</summary>
    public static void For<T>(IEnumerable<T> items, Action<T> render)
    {
        int i = 0;
        foreach (var item in items)
        {
            ImGui.PushID(i);
            render(item);
            ImGui.PopID();
            i++;
        }
    }

    /// <summary>Iterate items with index, stable IDs auto-pushed.</summary>
    public static void For<T>(IEnumerable<T> items, Action<T, int> render)
    {
        int i = 0;
        foreach (var item in items)
        {
            ImGui.PushID(i);
            render(item, i);
            ImGui.PopID();
            i++;
        }
    }

    /// <summary>Iterate a count-based loop with stable IDs.</summary>
    public static void For(int count, Action<int> render)
    {
        for (int i = 0; i < count; i++)
        {
            ImGui.PushID(i);
            render(i);
            ImGui.PopID();
        }
    }

    /// <summary>Conditionally render content. Equivalent to <c>if (cond) action()</c> but readable inline.</summary>
    public static void When(bool cond, Action render)
    {
        if (cond) render();
    }

    /// <summary>Render one branch or the other.</summary>
    public static void Switch(bool cond, Action onTrue, Action onFalse)
    {
        (cond ? onTrue : onFalse)();
    }
}
