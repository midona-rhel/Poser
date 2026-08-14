using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Poser.Config;

namespace Poser.Tests.Core;

/// <summary>
/// The three promises the dual-slot keybinds make: an existing config keeps
/// working, a preset says something complete about every action, and a chord
/// bound twice is reported on both of its rows.
/// </summary>
public class KeybindTests
{
    // ── migration ────────────────────────────────────────────────────────

    [Fact]
    public void MigrationMovesTheStoredChordIntoThePrimarySlot()
    {
        var ui = new UIConfiguration();
        ui.Keybinds["Undo"] = "Ctrl+Z";
        ui.Keybinds["Hide UI"] = "Alt+H";

        ui.MigrateKeybindsToSlots();

        Assert.Equal("Ctrl+Z", ui.Bindings["Undo"].Primary);
        Assert.Equal(string.Empty, ui.Bindings["Undo"].Secondary);
        Assert.Equal("Alt+H", ui.Bindings["Hide UI"].Primary);
        Assert.Empty(ui.Keybinds);
    }

    [Fact]
    public void MigrationLeavesABindingTheUserAlreadyEditedAlone()
    {
        var ui = new UIConfiguration();
        ui.Bindings["Undo"] = new KeybindSlots("Ctrl+W", "Alt+W");
        ui.Keybinds["Undo"] = "Ctrl+Z";

        ui.MigrateKeybindsToSlots();

        Assert.Equal("Ctrl+W", ui.Bindings["Undo"].Primary);
        Assert.Equal("Alt+W", ui.Bindings["Undo"].Secondary);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        var ui = new UIConfiguration();
        ui.Keybinds["Redo"] = "Ctrl+Y";

        ui.MigrateKeybindsToSlots();
        ui.MigrateKeybindsToSlots();

        Assert.Equal("Ctrl+Y", ui.Bindings["Redo"].Primary);
        Assert.Single(ui.Bindings);
    }

    [Fact]
    public void AnUnboundStoredChordDoesNotBecomeABinding()
    {
        var ui = new UIConfiguration();
        ui.Keybinds["Undo"] = string.Empty;

        ui.MigrateKeybindsToSlots();

        Assert.Empty(ui.Bindings);
    }

    // ── presets ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(KeybindPreset.Poser)]
    [InlineData(KeybindPreset.Brio)]
    [InlineData(KeybindPreset.Ktisis)]
    public void EveryPresetStatesEveryRegisteredAction(KeybindPreset preset)
    {
        var bindings = KeybindRegistry.Bindings(preset);

        Assert.Equal(KeybindRegistry.Actions.Count, bindings.Count);
        foreach (var action in KeybindRegistry.Actions)
            Assert.True(bindings.ContainsKey(action.Id), action.Id);
    }

    [Fact]
    public void ThePresetsCarryTheirReferenceToolsChords()
    {
        var poser = KeybindRegistry.Bindings(KeybindPreset.Poser);
        var brio = KeybindRegistry.Bindings(KeybindPreset.Brio);
        var ktisis = KeybindRegistry.Bindings(KeybindPreset.Ktisis);

        // Brio: KeyConfig(Z, requireShift: false, requireCtrl: true).
        Assert.Equal("Ctrl+Y", brio["Redo"].Primary);
        // Ktisis: KeyCombo(Z, CONTROL, SHIFT).
        Assert.Equal("Ctrl+Shift+Z", ktisis["Redo"].Primary);
        Assert.Equal("Ctrl+Y", poser["Redo"].Primary);
        // Ktisis: KeyCombo(T, CONTROL) against Poser's own Ctrl+1.
        Assert.Equal("Ctrl+T", ktisis["Translate mode"].Primary);
        Assert.Equal("Ctrl+1", poser["Translate mode"].Primary);
        // Brio ships its gizmo operations as NO_KEY, so the preset does too.
        Assert.Equal(string.Empty, brio["Translate mode"].Primary);
        // Brio: KeyConfig(M, requireShift: true) is Shift+M, not Ctrl+M.
        Assert.Equal("Shift+M", brio["Freeze all actors"].Primary);
        // Ktisis: KeyCombo(OEM_6) / KeyCombo(OEM_4).
        Assert.Equal("]", ktisis["Next camera"].Primary);
        Assert.Equal("[", ktisis["Previous camera"].Primary);
    }

    [Fact]
    public void APresetOverwritesTheSecondarySlotToo()
    {
        // Poser ships Redo with a second chord; a preset that has no second
        // chord for it must not leave the old one behind.
        Assert.Equal(
            "Ctrl+Shift+Z",
            KeybindRegistry.Bindings(KeybindPreset.Poser)["Redo"].Secondary);
        Assert.Equal(
            string.Empty,
            KeybindRegistry.Bindings(KeybindPreset.Brio)["Redo"].Secondary);
    }

    [Fact]
    public void EveryPresetChordParsesBackToItself()
    {
        foreach (var preset in new[]
        {
            KeybindPreset.Poser, KeybindPreset.Brio, KeybindPreset.Ktisis,
        })
        {
            foreach (var (action, slots) in KeybindRegistry.Bindings(preset))
            {
                for (int slot = 0; slot < 2; slot++)
                {
                    string chord = slots[slot];
                    if (chord.Length == 0)
                        continue;
                    Assert.Equal(chord, KeyChord.Parse(chord).ToString());
                }
            }
        }
    }

    [Fact]
    public void NoPresetShipsAConflict()
    {
        foreach (var preset in new[]
        {
            KeybindPreset.Poser, KeybindPreset.Brio, KeybindPreset.Ktisis,
        })
            Assert.Empty(
                KeybindRegistry.Conflicts(KeybindRegistry.Bindings(preset)));
    }

    [Fact]
    public void AnActionMissingFromTheStoredConfigFallsBackToItsDefault()
    {
        var resolved = KeybindRegistry.Resolve(
            new Dictionary<string, KeybindSlots>());

        Assert.Equal("Ctrl+Z", resolved["Undo"].Primary);
        Assert.Equal(string.Empty, resolved["Next tab"].Primary);
    }

    [Fact]
    public void AStoredEmptySlotStaysUnboundRatherThanReturningToTheDefault()
    {
        var resolved = KeybindRegistry.Resolve(
            new Dictionary<string, KeybindSlots>
            {
                ["Undo"] = new(string.Empty),
            });

        Assert.Equal(string.Empty, resolved["Undo"].Primary);
    }

    // ── conflicts ────────────────────────────────────────────────────────

    [Fact]
    public void TwoActionsOnOneChordFlagBothRows()
    {
        var conflicts = KeybindRegistry.Conflicts(
            new Dictionary<string, KeybindSlots>
            {
                ["Undo"] = new("Ctrl+Z"),
                ["Redo"] = new("Ctrl+Z"),
            });

        Assert.Equal(2, conflicts.Count);
        Assert.Equal(
            ["Redo"],
            conflicts[new KeybindRegistry.SlotRef("Undo", 0)]);
        Assert.Equal(
            ["Undo"],
            conflicts[new KeybindRegistry.SlotRef("Redo", 0)]);
    }

    [Fact]
    public void ASecondarySlotConflictsWithAPrimaryOne()
    {
        var conflicts = KeybindRegistry.Conflicts(
            new Dictionary<string, KeybindSlots>
            {
                ["Undo"] = new("Ctrl+Z"),
                ["Hide UI"] = new("Ctrl+H", "Ctrl+Z"),
            });

        Assert.True(
            conflicts.ContainsKey(new KeybindRegistry.SlotRef("Hide UI", 1)));
        Assert.False(
            conflicts.ContainsKey(new KeybindRegistry.SlotRef("Hide UI", 0)));
    }

    [Fact]
    public void OneActionsTwoSlotsConflictWithEachOther()
    {
        var conflicts = KeybindRegistry.Conflicts(
            new Dictionary<string, KeybindSlots>
            {
                ["Undo"] = new("Ctrl+Z", "ctrl+z"),
            });

        Assert.Equal(2, conflicts.Count);
    }

    [Fact]
    public void UnboundSlotsNeverConflict()
    {
        var conflicts = KeybindRegistry.Conflicts(
            new Dictionary<string, KeybindSlots>
            {
                ["Undo"] = new(string.Empty, string.Empty),
                ["Redo"] = new(string.Empty, string.Empty),
            });

        Assert.Empty(conflicts);
    }

    // ── chord vocabulary ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Shift+Z")]
    [InlineData("Ctrl+Shift+Alt+F12")]
    [InlineData("Ctrl+1")]
    [InlineData("Escape")]
    [InlineData("[")]
    [InlineData("PageUp")]
    public void CanonicalChordTextSurvivesARoundTrip(string chord) =>
        Assert.Equal(chord, KeyChord.Parse(chord).ToString());

    [Fact]
    public void ChordTextIsCaseInsensitiveOnTheWayIn() =>
        Assert.Equal("Ctrl+Z", KeyChord.Parse("ctrl+z").ToString());

    [Fact]
    public void ARawVirtualKeyNameFromAHandEditedConfigStillParses() =>
        Assert.Equal("[", KeyChord.Parse("OEM_4").ToString());

    [Fact]
    public void UnrecognisedTextIsUnboundRatherThanAModifierOnlyChord()
    {
        var chord = KeyChord.Parse("Ctrl+Nonsense");

        Assert.False(chord.IsBound);
        Assert.Equal(string.Empty, chord.ToString());
    }

    [Fact]
    public void EmptyTextIsUnbound() =>
        Assert.False(KeyChord.Parse(string.Empty).IsBound);

    [Fact]
    public void TheDigitKeysAreCapturable()
    {
        // The shipped gizmo binds are Ctrl+1..Ctrl+4; a capture scan that
        // could not see a digit could never rebind them.
        var keys = KeyChord.CapturableKeys().ToList();

        Assert.Contains(Dalamud.Bindings.ImGui.ImGuiKey.Key1, keys);
        Assert.Equal(
            VirtualKey.KEY_1,
            KeyChord.FromImGui(Dalamud.Bindings.ImGui.ImGuiKey.Key1));
    }

    [Fact]
    public void EveryCapturableKeyRoundTripsToAChord()
    {
        foreach (var key in KeyChord.CapturableKeys())
        {
            var virtualKey = KeyChord.FromImGui(key);
            Assert.NotNull(virtualKey);
            var chord = new KeyChord(false, false, false, virtualKey!.Value);
            Assert.NotEqual(string.Empty, chord.ToString());
            Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
        }
    }
}
