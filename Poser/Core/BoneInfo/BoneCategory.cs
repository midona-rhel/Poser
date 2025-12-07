namespace Poser.Core.BoneInfo;

/// <summary>
/// Categories for grouping bones in the UI.
/// </summary>
public enum BoneCategory
{
    Root,
    Head,       // Includes face, hair, ears
    Spine,      // Includes chest, IVCS torso, physics torso
    LeftArm,    // Includes left hand, IVCS fingers
    RightArm,   // Includes right hand, IVCS fingers
    LeftLeg,    // Includes IVCS toes, physics thigh
    RightLeg,   // Includes IVCS toes, physics thigh
    Tail,
    Equipment,  // Includes clothing
    Other
}
