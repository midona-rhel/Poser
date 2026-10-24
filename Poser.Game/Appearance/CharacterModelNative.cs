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

    internal static void InitializeCatalogModel(Character* character, int modelId)
    {
        if (modelId == 0) return;

        // The seed is a player, not the catalog model's appearance. Even a
        // hidden inherited weapon changes the monster's ground shadow (#622:
        // clearing/restoring only the main-hand model reproduced both states).
        // Clear model IDs before first draw; do not clear native weapon structs
        // or their owned pointers. Ordinary duplicates never enter this path.
        for (int slot = 0; slot < 3; slot++)
            character->DrawData.Weapon((DrawDataContainer.WeaponSlot)slot).ModelId = default;
        SetModelId(character, modelId);
    }

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
