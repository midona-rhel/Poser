using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Poser.Game.Natives;

/// <summary>Native release of a directly owned scene allocation, including rollback.</summary>
internal static unsafe class OwnedSceneObjectLifetime
{
    // Caller proves ownership/current incarnation before entering. ClientObjectManager
    // actors and borrowed world objects must never use this direct-allocation path.
    internal static void Destroy(SceneObject* owned)
    {
        if (owned == null) return;
        // Brio BGOObject.Destroy ordering; virtual dispatch preserves the weapon,
        // background-object or VFX implementation. Cleanup must precede free.
        owned->CleanupRender();
        owned->Dtor(1);
    }
}
