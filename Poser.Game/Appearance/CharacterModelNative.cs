using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Poser.Game.Appearance;

/// <summary>
/// Sole native mutation boundary for character model IDs and owned render slots.
/// Callers resolve the exact live body on the framework thread before entering.
/// Spawn and redraw orchestration stays with their lifetime owners.
/// </summary>
internal static unsafe class CharacterModelNative
{
    internal static void SetModelId(Character* character, int modelId) =>
        character->ModelContainer.ModelCharaId = modelId;

    internal static void ReleaseSlot(CharacterBase* body, int slot)
    {
        if (slot < 0 || slot >= body->SlotCount) return;
        var model = body->Models[slot];
        if (model == null) return;

        // Detach before releasing: cleanup may destroy the model. Release the
        // MODEL, not its resource handle; native cleanup owns its skeleton and
        // resource references. Overwriting RefCount skips that cleanup and leaves
        // an animation-list skeleton pointing at a freed CharacterBase.
        body->Models[slot] = null;
        model->DecRef();
    }
}
