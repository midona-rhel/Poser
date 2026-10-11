using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Core;

/// <summary>
/// The GPose object-table band and the small native reads every caller
/// around it repeated. Penumbra's CutsceneStart is slot 200, the game's own
/// GPose UI copy; GPose actors (the player's copy first) occupy 201-439.
/// </summary>
internal static unsafe class GPoseObjectTable
{
    /// <summary>The game's own GPose UI copy: in the cutscene range, never a posable actor.</summary>
    public const int UiCopyIndex = 200;

    /// <summary>The first GPose actor slot: the player's GPose copy.</summary>
    public const int FirstActorIndex = 201;

    public const int LastActorIndex = 439;

    /// <summary>Whether <paramref name="objectIndex"/> is a GPose actor slot (201-439).</summary>
    public static bool IsActorIndex(int objectIndex) =>
        objectIndex is >= FirstActorIndex and <= LastActorIndex;

    /// <summary>The object-table index of a live game object.</summary>
    public static int IndexOf(nint gameObject) => ((GameObject*)gameObject)->ObjectIndex;

    /// <summary>The model as a Human, or null when it is absent or another model type.</summary>
    public static Human* AsHuman(CharacterBase* model) =>
        model != null && model->GetModelType() == CharacterBase.ModelType.Human
            ? (Human*)model
            : null;

    /// <summary><see cref="AsHuman(CharacterBase*)"/> for an untyped draw object.</summary>
    public static Human* AsHuman(DrawObject* drawn) =>
        drawn != null && drawn->Object.GetObjectType() == ObjectType.CharacterBase
            ? AsHuman((CharacterBase*)drawn)
            : null;

    /// <summary>The address of the game's GPose controller, or zero before the event framework exists.</summary>
    public static nint ControllerAddress()
    {
        var framework = EventFramework.Instance();
        return framework == null ? nint.Zero : (nint)(&framework->EventSceneModule.EventGPoseController);
    }

    /// <summary>Hands a spawned character to the game's GPose controller.</summary>
    public static void AddCharacterToGPose(Character* character)
    {
        var framework = EventFramework.Instance();
        if (framework != null)
            framework->EventSceneModule.EventGPoseController.AddCharacterToGPose(character);
    }
}
