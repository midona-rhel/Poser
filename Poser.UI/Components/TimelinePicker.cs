using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// The animation surface's picker row. It is NOT another picker: it is the
/// ONE <c>PickerCell</c> the Appearance rows mount, told the optional things
/// a catalog row needs — its own query, an icon slot, a mono badge, and the
/// head strips that narrow the search — so a fix to the picker is a fix to
/// every picker.
/// </summary>
public static partial class Crystarium
{
    /// <summary>
    /// A picker row with an ARBITRARY action cluster, whose trigger owns the
    /// picker surface. The row's own actions take what they need and the
    /// trigger fills the rest, exactly as the legacy bridge row did.
    ///
    /// <para><paramref name="entries"/> is told the surface's live query and
    /// answers with the whole visible list: matching a timeline id, dropping
    /// kinds a slot can never hold and narrowing by weapon state are the
    /// CALLER's query logic, and none of it is expressible as a predicate over
    /// a row's name. The callback is invoked every frame the row is declared,
    /// so a caller memoizes it.</para>
    /// </summary>
    public static UiNode FormTimelinePicker<T>(
        string label,
        string value,
        Func<string, IReadOnlyList<T>> entries,
        Func<T, string> itemLabel,
        Func<T, string> itemKey,
        Func<T, long> itemContentKey,
        Func<T, nint> itemTexture,
        Func<T, TablerIcon?> itemGlyph,
        Func<T, string?> itemBadge,
        string? selectedKey,
        PickerSegment? kinds,
        PickerSegment? weapons,
        Action onOpen,
        Action<T> onPick,
        UiChildren actions = default,
        string? loadError = null,
        string? help = null,
        string? triggerHelp = null,
        bool disabled = false,
        UiKey key = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entries);
        // Captionless, like the accepted single-select: the form row's own
        // label already names the pick (user 2026-08-03).
        PickerProps<T> props = new(
            value, null, [], itemLabel, itemKey, selectedKey, null,
            loadError, onPick, null, onOpen, Dense: true, Disabled: disabled,
            DisabledHelp: triggerHelp, Multi: false, TriggerWidth: UiDim.Fill,
            Query: entries,
            ItemTexture: itemTexture,
            ItemGlyph: itemGlyph,
            ItemBadge: itemBadge,
            ItemContentKey: itemContentKey,
            // A strip nobody stated declares nothing: a slot that can hold one
            // kind offers no kind filter, and only the base destination
            // narrows by weapon state.
            Segment: kinds,
            SecondSegment: weapons,
            // The catalog surface is the WIDE panel: a row carries an icon, a
            // name and a badge, and the narrow picker cuts all three.
            Width: ActiveTheme.Picker.WideWidth);
        return FormRow(
            label,
            new Row
            {
                Sheet = SheetFamily.ActionGroupFill,
                Children =
                [
                    PickerSurface(in props, PickerKey(key, "timeline-picker")),
                    actions.Count == 0
                        ? UiNode.None
                        : new Row
                        {
                            Sheet = SheetFamily.ActionGroup,
                            Children = actions,
                        },
                ],
            },
            help,
            key);
    }
}
