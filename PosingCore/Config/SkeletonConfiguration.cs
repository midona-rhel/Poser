using System.Collections.Generic;
using Poser.Services;

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

    /// <summary>
    /// The shape the whole armature is drawn in. It lives HERE, and
    /// <c>EditorState</c> reads and writes it rather than holding its own copy,
    /// because it is a standing preference about how the overlay LOOKS — not
    /// about what is being edited right now — and the user put it in Settings
    /// rather than on the toolbar (user 2026-08-14). The default is the value
    /// the editor carried while it was session state, so an existing config
    /// keeps drawing exactly what it drew.
    /// </summary>
    public SkeletonViewMode SkeletonViewMode { get; set; } =
        SkeletonViewMode.Default;

    /// <summary>Draw only the bones that are selected. Persisted beside
    /// <see cref="SkeletonViewMode"/>, for the same reason and under the same
    /// default rule.</summary>
    public bool ShowSelectedBonesOnly { get; set; } = false;

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

    /// <summary>Which reference the overlapping-bone pick list behaves like.
    /// The default is what Poser already did, so the option changes nothing
    /// until it is asked to.</summary>
    public BonePickBehavior BonePickBehavior { get; set; } =
        BonePickBehavior.Ktisis;

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

/// <summary>
/// How picking works when several bone dots overlap under the cursor.
///
/// <para>The two references AGREE on the surface a cluster raises and on when
/// it comes and goes: a list of every candidate at the cursor, up on the first
/// hovered dot with no overlap threshold and no delay, following the pointer,
/// taking no input of its own, and gone the frame the pointer leaves. Both
/// hands are in the user base for what happens NEXT, which is where they
/// part — so the surface is shared and only the picking differs.</para>
/// </summary>
public enum BonePickBehavior
{
    /// <summary>
    /// Ktisis (<c>Ktisis/Interface/Overlay/SelectableGui.cs:101-158</c>): the
    /// list is the whole mechanism. The wheel moves a HIGHLIGHT through it and
    /// costs nothing; a click on the dots commits whatever the highlight is
    /// on. The highlight is carried between clusters rather than reset, and
    /// there is no second surface. Poser's default.
    /// </summary>
    Ktisis,

    /// <summary>
    /// Brio (<c>Brio/UI/Windows/Specialized/PosingOverlayWindow.cs:346-450</c>):
    /// the list is a passive readout marking whatever is SELECTED, and picking
    /// happens through a second surface. A click or a wheel notch over a
    /// cluster selects the nearest candidate outright and opens an anchored
    /// pick popup; inside it the wheel walks the selection one entry per notch
    /// and a row click stops on one. The popup outlives the hover that opened
    /// it, takes the dots out of play while it is up, and closes on Escape, a
    /// press outside it, or a picked row.
    /// </summary>
    Brio,
}

/// <summary>Which actor the overlay treats as "the active one" when
/// <see cref="SkeletonConfiguration.DimInactiveActors"/> is on — Ktisis'
/// <c>ActiveState</c>.</summary>
public enum ActiveActorSource
{
    Target,
    Selection,
    Both,
}
