using System.Collections.Generic;
using System.Linq;
using Poser.Config;
using Poser.Documents.Config;

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
}
