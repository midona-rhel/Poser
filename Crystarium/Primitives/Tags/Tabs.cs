using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>One tab definition for <see cref="Crystarium.Tabs"/>.</summary>
public readonly struct Tab
{
    public readonly string Label;
    public readonly Action Body;

    public Tab(string label, Action body) { Label = label; Body = body; }
}

public static partial class Crystarium
{
    /// <summary>
    /// Horizontal tab bar with a content panel. Returns the (possibly updated) selected index.
    /// <code>
    ///   selected = Crystarium.Tabs("##tabs", selected, new[] {
    ///       new Tab("General",  () => Crystarium.Text("General body")),
    ///       new Tab("Display",  () => Crystarium.Text("Display body")),
    ///       new Tab("Advanced", () => Crystarium.Text("Advanced body")),
    ///   });
    /// </code>
    /// </summary>
    public static int Tabs(string id, int selected, IReadOnlyList<Tab> tabs)
    {
        Stylesheet.EnsureInitialized();
        if (tabs.Count == 0) return selected;
        selected = Math.Clamp(selected, 0, tabs.Count - 1);

        // Tab strip — row of buttons
        Element(new ElementProps
        {
            Id = id + "_strip",
            Style = new ElementStyle
            {
                FlexDirection = FlexDirection.Row,
                Gap = Theme.Spacing.Xs,
                Margin = new Spacing(0, 0, Theme.Spacing.Sm, 0),
            },
        }, () =>
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                int idx = i;
                bool isActive = idx == selected;
                int captured = selected;
                Element(new ElementProps
                {
                    Id = $"{id}_tab_{idx}",
                    Classes = isActive ? Cls.Btn + "primary" : Cls.Btn,
                    Style = new ElementStyle
                    {
                        Width = Sizing.Auto,
                        Padding = new Spacing(0, Theme.Spacing.Md),
                        BackgroundColor = isActive ? Theme.Color.SurfaceRaised : Theme.Color.SurfaceSunken,
                        BorderRadius = Theme.Radius.Sm,
                    },
                    OnClick = () => captured = idx,
                }, () => Text(tabs[idx].Label));
                // capture-write back
                if (captured != selected) selected = captured;
            }
        });

        // Content panel
        tabs[selected].Body?.Invoke();
        return selected;
    }
}
