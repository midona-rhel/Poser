using System.Collections.Generic;

namespace Poser.Config;

/// <summary>
/// One named overlay bone-visibility set (Ktisis <c>PresetConfig.Presets</c>).
/// Bones are stored as CANONICAL NAMES rather than ids because a preset is
/// applied to whichever actor the user picks, and a stored list rather than a
/// keyed dictionary because the serializer must not have to reconstruct a
/// comparer to keep the store's case rules.
/// </summary>
public class BoneVisibilityPreset
{
    public string Name { get; set; } = string.Empty;
    public List<string> Bones { get; set; } = new();
}

/// <summary>
/// Configuration for skeleton overlay display settings.
/// </summary>
public class SkeletonConfiguration
{
    // Sizes
    public float BoneDotRadius { get; set; } = 3.4f;
    public float BoneLineThickness { get; set; } = 1.0f;
    public float BoneLineOpacity { get; set; } = 0.232f;
    public float BoneLineOpacityWhileUsing { get; set; } = 0.150f;
    public float OctahedraWidth { get; set; } = 4f;

    // Colors (uint ABGR format like Brio).
    // Selected/hovered default to the baked baseline accent (dark-theme
    // primary #3297FF) — this layer cannot see the theme. While the stored
    // value still equals its Default* constant, SkeletonOverlayWindow
    // substitutes the LIVE accent (theme + AccentIndex); an explicit
    // ColorWell override pins the stored value. Everything else is a
    // deliberate muted tone so unselected states recede over game scenery.
    public const uint DefaultSelectedBoneColor = 0xFFFF9732; // Accent #3297FF
    public const uint DefaultHoveredBoneColor = 0xFFFFBB7A;  // Accent +35% white #7ABBFF

    public uint BoneColor { get; set; } = 0xFFB8A394;        // Slate #94A3B8 — inactive dots + lines
    public uint BoneOutlineColor { get; set; } = 0xFF000000; // Black
    public uint SelectedBoneColor { get; set; } = DefaultSelectedBoneColor;
    public uint ModifiedBoneColor { get; set; } = 0xFF7CB563; // Jade #63B57C
    public uint HoveredBoneColor { get; set; } = DefaultHoveredBoneColor;
    public uint IkChainColor { get; set; } = 0xFF44A5D9;      // Amber #D9A544
    public uint MirroredBoneColor { get; set; } = 0xFFA07BC2; // Rose #C27BA0

    // Display options
    public bool ShowSkeletonLines { get; set; } = true;

    /// <summary>Named bone-visibility sets, shared by every actor and applied
    /// per actor. Kept sorted by name so the persisted file is stable.</summary>
    public List<BoneVisibilityPreset> BoneVisibilityPresets { get; set; } = new();

    /// <summary>
    /// Brio's <c>SkeletonLineToCircle</c>: a connector stops at the two dots'
    /// circle edges instead of running centre to centre. Brio ships it ON;
    /// Poser ships it OFF so a config written before this option existed keeps
    /// drawing what it drew — the same rule every option below follows.
    /// </summary>
    public bool SkeletonLineToCircle { get; set; } = false;

    /// <summary>Brio's <c>HideSkeletonWhenGizmoActive</c>: dots and lines both
    /// disappear for the duration of a gizmo drag rather than fading to
    /// <see cref="BoneLineOpacityWhileUsing"/>.</summary>
    public bool HideSkeletonWhileDragging { get; set; } = false;

    // ── inactive-actor dimming (Ktisis OverlayConfig) ────────────────────
    // Ktisis' DimOverlayForInactiveActors / InactiveOpacity / ActiveStateType,
    // applied to an actor's dots AND its connector lines together.

    public bool DimInactiveActors { get; set; } = false;
    public float InactiveActorOpacity { get; set; } = 0.5f;
    public ActiveActorSource ActiveActorSource { get; set; } =
        ActiveActorSource.Target;

    /// <summary>Ktisis' <c>ShowFriendlyBoneNames</c>. Off shows the raw
    /// skeleton name (<c>j_f_ago</c>) wherever a bone names itself.</summary>
    public bool ShowFriendlyBoneNames { get; set; } = true;

    /// <summary>
    /// Ktisis' <c>ShowAllVieraEars</c>. Unlike the options above this one keeps
    /// the REFERENCE default: the filtered state is the whole point of the
    /// feature — three of a Viera's four ear sets are never the ones the
    /// character wears — so it ships on and the switch is the escape.
    /// </summary>
    public bool ShowAllVieraEars { get; set; } = false;
}

/// <summary>Which actor the overlay treats as "the active one" when
/// <see cref="SkeletonConfiguration.DimInactiveActors"/> is on — Ktisis'
/// <c>ActiveState</c>.</summary>
public enum ActiveActorSource
{
    Target,
    Selection,
    Both,
>>>>>>> 05feabf (Overlay and gizmo options gain a home: two config sections carry the reference switches, the settings page grows a Gizmo category, and every default is what the tool already did)
}
