using System.Collections.Generic;
using System.Linq;
using Poser.Config;

namespace Poser.Tests.Core;

public sealed class KeybindTests
{
    [Fact]
    public void Pose_bindings_persist_rebinding_clearing_and_conflicts_without_new_defaults()
    {
        string[] actions = ["Import pose", "Import pose from file", "Export pose",
            "Export pose to file", "Copy pose", "Paste pose", "Play / pause actor"];
        foreach (var action in actions)
        {
            Assert.Contains(KeybindRegistry.Actions, entry => entry.Id == action);
            Assert.Empty(KeybindRegistry.Default(action).Primary);
        }
        var settings = new UIConfiguration();
        settings.Bindings["Import pose"] = new("Ctrl+I", "Alt+I");
        settings.Bindings["Paste pose"] = new("Ctrl+I");
        settings.Bindings["Copy pose"] = new("");
        var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<UIConfiguration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(settings))!;
        var resolved = KeybindRegistry.Resolve(loaded.Bindings);
        Assert.Equal("Alt+I", resolved["Import pose"].Secondary);
        Assert.Empty(resolved["Copy pose"].Primary);
        Assert.Equal(2, KeybindRegistry.Conflicts(resolved).Count);
        loaded.Bindings["Paste pose"].Primary = "Ctrl+V";
        Assert.Empty(KeybindRegistry.Conflicts(KeybindRegistry.Resolve(loaded.Bindings)));
    }

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
}
