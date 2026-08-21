namespace Poser.Game.Scene;

/// <summary>
/// The ONE next step a refused entity offers, keyed on the entity kind. A
/// refusal detail says what happened; this says what the user can do about it,
/// and both the result list and the operation log read it from here so they
/// can never disagree.
///
/// <para>It is deliberately keyed on the KIND rather than written at each of
/// the twenty-odd refusal sites: the reason varies with the failure, but the
/// recovery is a property of the thing that did not come back — a light is
/// rebuilt in the light editor whichever way its restore refused. A site with
/// a genuinely better step states its own and this leaves it alone.</para>
/// </summary>
internal static class SceneEntityRemedy
{
    /// <summary>The next step for one refused entity kind, or null when the
    /// kind has no step worth stating. Kinds are the literals the workflow
    /// builds its <see cref="SceneEntityOutcome"/>s with.</summary>
    public static string? For(string kind) => kind switch
    {
        "Actor" =>
            "The actor is in the scene. Select it and apply its pose from the "
            + "Scenes library, or load the scene again.",
        "Companion" =>
            "The companion is attached. Select the actor and apply the "
            + "companion pose again once its body has drawn.",
        "Animation" =>
            "Set what the actor is playing on its Animation tab.",
        "Character file" =>
            "Import the character file again on the actor's Appearance tab, "
            + "then save the scene so it records where the file is now.",
        "Gaze" =>
            "Set where the actor looks on its Pose tab.",
        "Object" =>
            "Spawn the object yourself, or free a spawn slot and load the "
            + "scene again.",
        "Overlay" =>
            "Stage the node again from the overlay browser.",
        "World object" =>
            "A map object only exists where it stands. Load this scene in the "
            + "zone it was taken in.",
        "Light" =>
            "Add the light yourself, or load the scene again with Actors "
            + "included so the actor it hangs off exists.",
        "Camera" =>
            "Add the camera yourself on the Camera tab.",
        "Live camera" =>
            "Choose which camera is live on the Camera tab.",
        "Environment" =>
            "Set the time, weather and sky yourself on the environment tabs.",
        "World" =>
            "Set the render and simulation toggles yourself on the "
            + "environment's World tab.",
        _ => null,
    };
}
