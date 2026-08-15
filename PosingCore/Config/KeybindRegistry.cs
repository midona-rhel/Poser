using System;
using System.Collections.Generic;

namespace Poser.Config;

/// <summary>One action's two chords. Both are ordinary config text
/// (<see cref="KeyChord"/>'s vocabulary); empty is unbound.</summary>
public sealed class KeybindSlots
{
    public string Primary { get; set; } = string.Empty;
    public string Secondary { get; set; } = string.Empty;

    public KeybindSlots() { }

    public KeybindSlots(string primary, string secondary = "")
    {
        Primary = primary;
        Secondary = secondary;
    }

    public KeybindSlots Copy() => new(Primary, Secondary);

    /// <summary>The chord in a slot: 0 is primary, 1 is secondary. Slots are
    /// addressed by index because the rebind UI is two identical columns and
    /// a bool would read as "the other one".</summary>
    public string this[int slot]
    {
        get => slot == 0 ? Primary : Secondary;
        set
        {
            if (slot == 0)
                Primary = value;
            else
                Secondary = value;
        }
    }
}

/// <summary>Which tool's chords a preset speaks.</summary>
public enum KeybindPreset
{
    Poser,
    Brio,
    Ktisis,
}

/// <summary>One bindable action: the id it persists under, the group its
/// settings row sits in, and the sentence its row explains itself with.</summary>
public sealed record KeybindAction(string Id, string Group, string Help);

/// <summary>
/// THE bindable-action list and the three chord sets it can be dressed in.
/// One home: the runtime binder builds its delegates against this order, the
/// settings page draws these rows, and the hover badges resolve through it —
/// an action that is not here is not bindable anywhere.
///
/// <para>Ids are the persisted keys and are frozen: the seven original ids
/// are the ones a pre-dual-slot config already holds, so renaming one would
/// silently unbind it.</para>
///
/// <para>Preset chords are transcribed from the reference sources —
/// Brio <c>Config/InputManagerConfiguration.cs</c>, Ktisis
/// <c>Actions/Handlers/**</c>. An action the reference has no counterpart for
/// keeps Poser's own default; where the reference deliberately ships the
/// action unbound, so does the preset.</para>
/// </summary>
public static class KeybindRegistry
{
    public const string GroupEditing = "EDITING";
    public const string GroupGizmo = "GIZMO";
    public const string GroupOverlay = "OVERLAY";
    public const string GroupWindows = "WINDOWS";
    public const string GroupScene = "SCENE";

    // ── actions ──────────────────────────────────────────────────────────

    public static IReadOnlyList<KeybindAction> Actions { get; } =
    [
        new("Undo", GroupEditing, "Undo the last move, rotation or scale"),
        new("Redo", GroupEditing, "Reapply the change you undid"),
        new("Deselect", GroupEditing, "Drop the current selection"),

        new("Translate mode", GroupGizmo, "Gizmo moves the selection"),
        new("Rotate mode", GroupGizmo, "Gizmo rotates the selection"),
        new("Scale mode", GroupGizmo, "Gizmo scales the selection"),
        new("Universal mode", GroupGizmo,
            "Gizmo moves, rotates and scales at once"),
        new("Cycle gizmo mode", GroupGizmo,
            "Step through move, rotate, scale and universal"),
        new("Toggle transform space", GroupGizmo,
            "Swap the gizmo between local and world axes"),
        new("Cycle rotation pivot", GroupGizmo,
            "Rotate in place, or orbit the parent bone"),
        new("Cycle symmetry", GroupGizmo,
            "Step through off, copy and mirror for paired bones"),

        new("Toggle bone overlay", GroupOverlay,
            "Show or hide the skeleton overlay"),
        new("Selected bones only", GroupOverlay,
            "Limit the overlay to the current selection"),
        new("Cycle skeleton view", GroupOverlay,
            "Step through dots, octahedra and joints"),

        new("Hide UI", GroupWindows, "Hide every Poser window, then bring them back"),
        new("Toggle workspace", GroupWindows, "Show or hide the main window"),
        new("Toggle settings", GroupWindows, "Open or close settings"),
        new("Toggle scene panel", GroupWindows,
            "Show or hide the scene sidebar window"),
        new("Open pose library", GroupWindows, "Show the pose library"),
        new("Next tab", GroupWindows, "Move to the next inspector tab"),
        new("Previous tab", GroupWindows, "Move to the previous inspector tab"),

        new("Next camera", GroupScene, "Make the next camera live"),
        new("Previous camera", GroupScene, "Make the previous camera live"),
        new("Freeze all actors", GroupScene, "Pause every actor's animation"),
        new("Resume all actors", GroupScene, "Resume every actor's animation"),
    ];

    // ── presets ──────────────────────────────────────────────────────────

    /// <summary>
    /// Poser's own chords. The seven originals are unchanged; the overlay
    /// toggle takes Ctrl+O because BOTH references ship that chord for it,
    /// and every other new action ships unbound. Redo carries a second
    /// default — Ctrl+Shift+Z, what a Ktisis user's hand already does —
    /// which is the whole point of a second slot. Deselect takes Escape,
    /// which is what both references bind it to (Brio's <c>Posing_Esc</c>,
    /// Ktisis's <c>Select_None</c>) and what the window no longer answers.
    /// </summary>
    private static readonly Dictionary<string, KeybindSlots> PoserDefaults =
        new(StringComparer.Ordinal)
        {
            ["Undo"] = new("Ctrl+Z"),
            ["Redo"] = new("Ctrl+Y", "Ctrl+Shift+Z"),
            ["Deselect"] = new("Escape"),
            ["Translate mode"] = new("Ctrl+1"),
            ["Rotate mode"] = new("Ctrl+2"),
            ["Scale mode"] = new("Ctrl+3"),
            ["Universal mode"] = new("Ctrl+4"),
            ["Hide UI"] = new("Ctrl+H"),
            ["Toggle bone overlay"] = new("Ctrl+O"),
        };

    /// <summary>
    /// Brio's <c>_defaultKeyBindings</c>. Read the constructor positionally:
    /// <c>KeyConfig(key, requireShift, requireCtrl, requireAlt)</c> — so
    /// <c>new KeyConfig(M, true)</c> is Shift+M, not Ctrl+M.
    ///
    /// <para>Brio ships its four gizmo-operation binds as NO_KEY, so this
    /// preset leaves them unbound rather than inventing chords for them.</para>
    /// </summary>
    private static readonly Dictionary<string, KeybindSlots> BrioDefaults =
        new(StringComparer.Ordinal)
        {
            ["Undo"] = new("Ctrl+Z"),
            ["Redo"] = new("Ctrl+Y"),
            ["Deselect"] = new("Escape"),
            ["Toggle bone overlay"] = new("Ctrl+O"),
            ["Toggle workspace"] = new("Ctrl+B"),
            ["Freeze all actors"] = new("Shift+M"),
            ["Resume all actors"] = new("Shift+N"),
        };

    /// <summary>
    /// Ktisis's <c>KeybindInfo.Default</c> combos. The toolbar window binds
    /// (F1–F8) map onto Poser's equivalent surfaces where one exists; the
    /// freecam movement binds have no Poser counterpart and are absent.
    /// </summary>
    private static readonly Dictionary<string, KeybindSlots> KtisisDefaults =
        new(StringComparer.Ordinal)
        {
            ["Undo"] = new("Ctrl+Z"),
            ["Redo"] = new("Ctrl+Shift+Z"),
            ["Deselect"] = new("Escape"),
            ["Translate mode"] = new("Ctrl+T"),
            ["Rotate mode"] = new("Ctrl+R"),
            ["Scale mode"] = new("Ctrl+S"),
            ["Universal mode"] = new("Ctrl+U"),
            ["Toggle transform space"] = new("Ctrl+X"),
            ["Toggle bone overlay"] = new("Ctrl+O"),
            ["Toggle workspace"] = new("F1"),
            ["Toggle scene panel"] = new("F7"),
            ["Toggle settings"] = new("F8"),
            ["Next camera"] = new("]"),
            ["Previous camera"] = new("["),
        };

    private static Dictionary<string, KeybindSlots> Table(
        KeybindPreset preset) => preset switch
        {
            KeybindPreset.Brio => BrioDefaults,
            KeybindPreset.Ktisis => KtisisDefaults,
            _ => PoserDefaults,
        };

    /// <summary>
    /// A COMPLETE binding set for the preset: every registered action, in
    /// registry order. A preset is a statement about all of them, so an
    /// action the reference does not have lands unbound rather than keeping
    /// whatever the user had — the exception being Poser's own defaults,
    /// which are what "unbound here" is measured against.
    /// </summary>
    public static Dictionary<string, KeybindSlots> Bindings(
        KeybindPreset preset)
    {
        var table = Table(preset);
        var result = new Dictionary<string, KeybindSlots>(
            Actions.Count, StringComparer.Ordinal);
        foreach (var action in Actions)
            result[action.Id] = table.TryGetValue(action.Id, out var slots)
                ? slots.Copy()
                : new KeybindSlots();
        return result;
    }

    /// <summary>The one unbound instance <see cref="SharedDefault"/> hands
    /// back for an action the shipped table does not name. Shared under the
    /// same read-only contract as the table's own entries.</summary>
    private static readonly KeybindSlots Unbound = new();

    /// <summary>
    /// Poser's shipped chords for one action, WITHOUT a defensive copy.
    ///
    /// <para>THE RETURNED OBJECT IS THE REGISTRY'S OWN AND MUST NOT BE
    /// MUTATED. <see cref="Default"/> is the copying accessor, and the only
    /// caller that needs it is the rebind UI, which edits what it is handed.
    /// The KEYBIND PUMP asks this question for every registered action on
    /// every framework tick, and a shipped configuration stores no bindings
    /// at all (<c>UIConfiguration.Bindings</c> starts empty), so the copy was
    /// one heap object per action per tick for the whole default install.
    /// </para>
    /// </summary>
    public static KeybindSlots SharedDefault(string actionId) =>
        PoserDefaults.TryGetValue(actionId, out var slots) ? slots : Unbound;

    /// <summary>Poser's shipped chords for one action, for a stored binding
    /// that has no entry at all (a config written before the action
    /// existed). A fresh copy the caller may edit; read-only callers on a
    /// hot path take <see cref="SharedDefault"/> instead.</summary>
    public static KeybindSlots Default(string actionId) =>
        SharedDefault(actionId).Copy();

    /// <summary>The stored bindings filled out to the whole registry, so
    /// every caller iterates one shape.</summary>
    public static Dictionary<string, KeybindSlots> Resolve(
        IReadOnlyDictionary<string, KeybindSlots> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var result = new Dictionary<string, KeybindSlots>(
            Actions.Count, StringComparer.Ordinal);
        foreach (var action in Actions)
            result[action.Id] = stored.TryGetValue(action.Id, out var slots)
                ? slots.Copy()
                : Default(action.Id);
        return result;
    }

    // ── conflicts ────────────────────────────────────────────────────────

    /// <summary>One slot of one action: the pair a conflict is reported
    /// against, and the pair the settings page addresses a row by.</summary>
    public readonly record struct SlotRef(string ActionId, int Slot);

    /// <summary>
    /// Every slot that shares its chord with another slot, mapped to the
    /// OTHER actions holding it. Both sides are reported — a conflict is a
    /// property of the pair, not of whichever row was edited last — and an
    /// action's own two slots conflict with each other exactly as two
    /// different actions do, because binding the same chord twice to one
    /// action is equally a mistake.
    ///
    /// <para>Unbound slots never conflict: "nothing" is not a chord.</para>
    /// </summary>
    public static Dictionary<SlotRef, IReadOnlyList<string>> Conflicts(
        IReadOnlyDictionary<string, KeybindSlots> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var holders = new Dictionary<string, List<SlotRef>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions)
        {
            if (!bindings.TryGetValue(action.Id, out var slots))
                continue;
            for (int slot = 0; slot < 2; slot++)
            {
                // Compared as PARSED chords rendered back to canonical text:
                // "ctrl+z" and "Ctrl+Z" are one binding, and unparseable text
                // is unbound on both sides of the comparison.
                string chord = KeyChord.Parse(slots[slot]).ToString();
                if (chord.Length == 0)
                    continue;
                if (!holders.TryGetValue(chord, out var list))
                    holders[chord] = list = [];
                list.Add(new SlotRef(action.Id, slot));
            }
        }

        var conflicts = new Dictionary<SlotRef, IReadOnlyList<string>>();
        foreach (var (_, list) in holders)
        {
            if (list.Count < 2)
                continue;
            foreach (var slot in list)
            {
                var others = new List<string>(list.Count - 1);
                foreach (var other in list)
                    if (other != slot && !others.Contains(other.ActionId))
                        others.Add(other.ActionId);
                conflicts[slot] = others;
            }
        }
        return conflicts;
    }
}
