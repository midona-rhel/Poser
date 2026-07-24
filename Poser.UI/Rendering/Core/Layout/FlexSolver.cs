using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// One flex child, fully pre-resolved to pixels. <see cref="NaturalWidth"/>
/// carries the Fixed value (pre-scaled) or the measured width for Auto;
/// Flex/Fill items get their width from the solver.
/// </summary>
public struct FlexItem
{
    public SizingMode Mode;
    /// <summary>px — used when <see cref="Mode"/> is Fixed or Auto.</summary>
    public float NaturalWidth;
    /// <summary>Weight for Flex (Sizing.Value) — Fill contributes 1.</summary>
    public float FlexWeight;
    public AlignSelf AlignSelf;
    /// <summary>px — a fixed cross-size enables non-stretch alignment.</summary>
    public float? FixedHeight;
}

public struct FlexParams
{
    public float InnerWidth;
    public float InnerHeight;
    public float Gap;
    public float RowGap;
    /// <summary>Container default when an item's AlignSelf is Auto.</summary>
    public Align AlignItems;
    public bool Wrap;
}

/// <summary>
/// Pure flex row solver — the three-pass algorithm extracted verbatim from the
/// v1 <c>Element.RunRowChildren</c> (Norvrandt/Layout/Row.cs): pack items into
/// lines (wrap), resolve remaining space per weight, place with per-item cross
/// alignment. Everything is pixels in, pixels out; no ImGui, no scale — the
/// caller pre-scales. Unit-testable headless.
/// </summary>
public static class FlexSolver
{
    /// <summary>
    /// Solves item rects (relative to the content origin) into
    /// <paramref name="rects"/>. Returns the number of lines.
    /// </summary>
    public static int Solve(ReadOnlySpan<FlexItem> items, in FlexParams p, Span<RectF> rects)
    {
        if (items.Length == 0) return 0;
        if (rects.Length < items.Length) throw new ArgumentException("rects span too small", nameof(rects));

        // ── pass 1+2: pack into lines. Flex/Fill items contribute 0 width to
        // wrap packing (their size depends on the line they land in).
        var lines = new List<(int Start, int Count)>();
        int lineStart = 0;
        int lineCount = 0;
        float consumed = 0f;
        for (int i = 0; i < items.Length; i++)
        {
            float w = items[i].Mode is SizingMode.Fixed or SizingMode.Auto ? items[i].NaturalWidth : 0f;
            if (p.Wrap && lineCount > 0 && consumed + p.Gap + w > p.InnerWidth)
            {
                lines.Add((lineStart, lineCount));
                lineStart = i;
                lineCount = 0;
                consumed = 0f;
            }
            lineCount++;
            consumed += (lineCount == 1 ? 0f : p.Gap) + w;
        }
        if (lineCount > 0) lines.Add((lineStart, lineCount));

        // ── pass 3: per line, resolve weights and place.
        float lineY = 0f;
        foreach (var (start, count) in lines)
        {
            float lineFixed = 0f, lineWeight = 0f, lineNaturalAuto = 0f;
            for (int j = start; j < start + count; j++)
            {
                switch (items[j].Mode)
                {
                    case SizingMode.Fixed: lineFixed += items[j].NaturalWidth; break;
                    case SizingMode.Auto:  lineNaturalAuto += items[j].NaturalWidth; break;
                    case SizingMode.Flex:  lineWeight += items[j].FlexWeight; break;
                    case SizingMode.Fill:  lineWeight += 1f; break;
                }
            }

            float lineGaps = p.Gap * (count - 1);
            float remaining = p.InnerWidth - lineFixed - lineNaturalAuto - lineGaps;
            float perWeight = lineWeight > 0f ? remaining / lineWeight : 0f;

            float x = 0f;
            for (int j = start; j < start + count; j++)
            {
                ref readonly var item = ref items[j];
                float w = item.Mode switch
                {
                    SizingMode.Fixed => item.NaturalWidth,
                    SizingMode.Auto  => item.NaturalWidth,
                    SizingMode.Flex  => item.FlexWeight * perWeight,
                    SizingMode.Fill  => perWeight,
                    _ => 0f,
                };

                var effective = item.AlignSelf switch
                {
                    AlignSelf.Start   => Align.Start,
                    AlignSelf.Center  => Align.Center,
                    AlignSelf.End     => Align.End,
                    AlignSelf.Stretch => Align.Stretch,
                    _ => p.AlignItems,
                };

                float itemY = lineY;
                float itemHeight = p.InnerHeight;
                if (effective != Align.Stretch && item.FixedHeight.HasValue)
                {
                    itemHeight = item.FixedHeight.Value;
                    itemY = effective switch
                    {
                        Align.Start  => lineY,
                        Align.Center => lineY + (p.InnerHeight - itemHeight) / 2f,
                        Align.End    => lineY + p.InnerHeight - itemHeight,
                        _ => lineY,
                    };
                }

                rects[j] = new RectF(x, itemY, w, itemHeight);
                x += w + p.Gap;
            }

            lineY += p.InnerHeight + p.RowGap;
        }

        return lines.Count;
    }
}
