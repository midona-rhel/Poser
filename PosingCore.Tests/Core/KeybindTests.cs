using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Poser.Config;

namespace Poser.Tests.Core;

public sealed class KeybindTests
{
    [Fact]
    public void Keybind_migration_is_idempotent_and_preserves_user_edited_slots()
    {
        var ui = new UIConfiguration();
        ui.Keybinds["Undo"] = "Ctrl+Z";
        ui.Keybinds["Redo"] = "Ctrl+Y";
        ui.Bindings["Undo"] = new KeybindSlots("Ctrl+W", "Alt+W");

        ui.MigrateKeybindsToSlots();
        ui.MigrateKeybindsToSlots();

        Assert.Equal("Ctrl+W", ui.Bindings["Undo"].Primary);
        Assert.Equal("Alt+W", ui.Bindings["Undo"].Secondary);
        Assert.Equal("Ctrl+Y", ui.Bindings["Redo"].Primary);
        Assert.Empty(ui.Keybinds);
    }

    [Fact]
    public void Keybind_vocabulary_preserves_canonical_round_trips_and_preset_completeness()
    {
        foreach (var text in new[] { "Ctrl+Z", "Ctrl+Shift+Alt+F12", "[", "PageUp" })
            Assert.Equal(text, KeyChord.Parse(text).ToString());
        Assert.False(KeyChord.Parse("Ctrl+Nonsense").IsBound);
        Assert.Equal("[", KeyChord.Parse("OEM_4").ToString());
        Assert.Contains(Dalamud.Bindings.ImGui.ImGuiKey.Key1, KeyChord.CapturableKeys());
        Assert.All(new[] { KeybindPreset.Poser, KeybindPreset.Brio, KeybindPreset.Ktisis },
            preset => Assert.Equal(KeybindRegistry.Actions.Count,
                KeybindRegistry.Bindings(preset).Count));
    }

    [Fact]
    public void Keybind_resolution_distinguishes_defaults_empty_slots_and_two_sided_conflicts()
    {
        var resolved = KeybindRegistry.Resolve(new Dictionary<string, KeybindSlots>
        {
            ["Undo"] = new(string.Empty),
        });
        var conflicts = KeybindRegistry.Conflicts(new Dictionary<string, KeybindSlots>
        {
            ["Undo"] = new("Ctrl+Z"),
            ["Redo"] = new("Ctrl+Z"),
        });

        Assert.Equal(string.Empty, resolved["Undo"].Primary);
        Assert.Equal(string.Empty, resolved["Next tab"].Primary);
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(new KeybindRegistry.SlotRef("Undo", 0), conflicts.Keys);
        Assert.Contains(new KeybindRegistry.SlotRef("Redo", 0), conflicts.Keys);
        Assert.Equal("Ctrl+Y", KeybindRegistry.Bindings(KeybindPreset.Brio)["Redo"].Primary);
    }
}
