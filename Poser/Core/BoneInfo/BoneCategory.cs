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

/// <summary>
/// Subcategories for finer grouping within categories (primarily for Head).
/// </summary>
public enum BoneSubcategory
{
    None,           // No subcategory (default)

    // Head subcategories
    Face,           // General face structure
    LeftEye,        // Left eye and eyelid
    RightEye,       // Right eye and eyelid
    Eyebrows,       // Both eyebrows
    Nose,           // Nose and bridge
    Mouth,          // Lips, jaw, tongue, teeth
    Cheeks,         // Cheek bones
    Hair,           // Hair bones
    Ears,           // Ear bones

    // Arm subcategories
    Hand,           // Hand bones
    Fingers,        // Finger bones

    // Leg subcategories
    Foot,           // Foot bones
    Toes,           // Toe bones
}
