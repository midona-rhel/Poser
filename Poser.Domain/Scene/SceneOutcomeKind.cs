namespace Poser.Domain.Scene;

/// <summary>What one scene outcome row is about: an entity, or one restored
/// part of one. Every kind has a label and a next step, and both switches
/// below are exhaustive with no default arm — CS8509 is an error in this
/// project, so a new kind cannot ship without saying what the user can do
/// about it.</summary>
public enum SceneOutcomeKind
{
    Actor,
    ActorName,
    Companion,
    Animation,
    CharacterFile,
    Collection,
    Gaze,
    Ik,
    Object,
    Overlay,
    WorldObject,
    Light,
    Camera,
    LiveCamera,
    Environment,
    World,
    Parent,
    Group,
}

public static class SceneOutcomeKinds
{
    /// <summary>The row's kind as the result list and the log name it.</summary>
    public static string Label(this SceneOutcomeKind kind) => kind switch
    {
        SceneOutcomeKind.Actor => "Actor",
        SceneOutcomeKind.ActorName => "Actor name",
        SceneOutcomeKind.Companion => "Companion",
        SceneOutcomeKind.Animation => "Animation",
        SceneOutcomeKind.CharacterFile => "Character file",
        SceneOutcomeKind.Collection => "Collection",
        SceneOutcomeKind.Gaze => "Gaze",
        SceneOutcomeKind.Ik => "IK",
        SceneOutcomeKind.Object => "Object",
        SceneOutcomeKind.Overlay => "Overlay",
        SceneOutcomeKind.WorldObject => "World object",
        SceneOutcomeKind.Light => "Light",
        SceneOutcomeKind.Camera => "Camera",
        SceneOutcomeKind.LiveCamera => "Live camera",
        SceneOutcomeKind.Environment => "Environment",
        SceneOutcomeKind.World => "World",
        SceneOutcomeKind.Parent => "Parent",
        SceneOutcomeKind.Group => "Group",
    };

    /// <summary>
    /// The ONE next step a refused row offers. A refusal detail says what
    /// happened; this says what the user can do about it, and the result list
    /// and the operation log both read it from here so they never disagree.
    /// It is keyed on the kind rather than written at each refusal site: the
    /// reason varies with the failure, but the recovery is a property of the
    /// thing that did not come back. A site with a genuinely better step
    /// states its own and this leaves it alone.
    /// </summary>
    public static string Remedy(this SceneOutcomeKind kind) => kind switch
    {
        SceneOutcomeKind.Actor =>
            "The actor is in the scene. Select it and apply its pose from the "
            + "Scenes library, or undo the load and load it again.",
        SceneOutcomeKind.ActorName =>
            "Rename the actor from its context menu.",
        SceneOutcomeKind.Companion =>
            "The companion is attached. Select the actor and apply the "
            + "companion pose again once its body has drawn.",
        SceneOutcomeKind.Animation =>
            "Set what the actor is playing on its Animation tab.",
        SceneOutcomeKind.CharacterFile =>
            "Import the character file again on the actor's Appearance tab, "
            + "then save the scene so it records where the file is now.",
        SceneOutcomeKind.Collection =>
            "Choose the actor's Penumbra collection on its Appearance tab.",
        SceneOutcomeKind.Gaze =>
            "Set where the actor looks on its Pose tab.",
        SceneOutcomeKind.Ik =>
            "Set the actor's IK chains up again on its Pose tab.",
        SceneOutcomeKind.Object =>
            "Spawn the object yourself, or undo the load, free a spawn slot "
            + "and load it again.",
        SceneOutcomeKind.Overlay =>
            "Stage the node again from the overlay browser.",
        SceneOutcomeKind.WorldObject =>
            "Spawn the object again from the object browser.",
        SceneOutcomeKind.Light =>
            "Add the light yourself, or undo the load and load it again with "
            + "Actors included so the actor it hangs off exists.",
        SceneOutcomeKind.Camera =>
            "Add the camera yourself on the Camera tab.",
        SceneOutcomeKind.LiveCamera =>
            "Choose which camera is live on the Camera tab.",
        SceneOutcomeKind.Environment =>
            "Set the time, weather and sky yourself on the environment tabs.",
        SceneOutcomeKind.World =>
            "Set the render and simulation toggles yourself on the "
            + "environment's World tab.",
        SceneOutcomeKind.Parent =>
            "Parent the entity again from its transform controls.",
        SceneOutcomeKind.Group =>
            "Group or reorder the entities again in the sidebar.",
    };
}
