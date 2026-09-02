using Dalamud.Configuration;
using Poser.Library;

namespace Poser.Config;

/// <summary>
/// Main configuration for Poser plugin.
/// Implements IPluginConfiguration for Dalamud persistence.
/// </summary>
public class PoserConfiguration : IPluginConfiguration
{
    // 2: overlay color redesign — stored overlay colors reset once on load
    // (ConfigurationService.MigrateConfig); sizes/opacity keep user values.
    // 3: keybinds gain a second slot — the stored single chord becomes the
    // action's primary (UIConfiguration.MigrateKeybindsToSlots).
    public int Version { get; set; } = LatestVersion;

    /// <summary>The version a config written by THIS build carries. A stored
    /// config below it goes through <c>ConfigurationService.MigrateConfig</c>
    /// once, in ascending step order.</summary>
    public const int LatestVersion = 5;

    public SkeletonConfiguration Skeleton { get; set; } = new();
    public GizmoConfiguration Gizmo { get; set; } = new();
    public DisplayConfiguration Display { get; set; } = new();
    public UIConfiguration UI { get; set; } = new();
    public IntegrationConfiguration Integration { get; set; } = new();
    public LibraryConfiguration Library { get; set; } = new();
    public AutoSaveConfiguration AutoSave { get; set; } = new();
    public CameraConfiguration Camera { get; set; } = new();
    public TransformConfiguration Transform { get; set; } = new();

    /// <summary>The pinned reference pictures and where they sit. Additive —
    /// a config written before this existed deserialises to an empty roster,
    /// which is what it had — so it needs no migration step.</summary>
    public ReferenceImageConfiguration ReferenceImages { get; set; } = new();

    // Behavior (Settings -> General)
    public bool OpenOnGPoseEnter { get; set; } = true;
    public bool CloseWithGPose { get; set; } = false;

    // Import behavior. The FILES "Freeze actor" checkbox writes this back, so
    // the persisted value IS the checkbox default — Brio's hidden
    // Posing.FreezeActorOnPoseImport config and its popup checkbox collapse
    // into the one visible surface. Default off, matching Brio's.
    public bool FreezeActorOnPoseImport { get; set; } = false;

    // Target sync (Brio parity): the GPose target drives selection by default
    // (Brio ships BrioTargetChangesWithGPose = true); the reverse defaults off
    // exactly like Brio's GPoseTargetChangesWithBrio — the sidebar already has
    // an explicit "Set game target" action.
    public bool SelectionChangesGPoseTarget { get; set; } = false;
    public bool GPoseTargetChangesSelection { get; set; } = true;


    /// <summary>
    /// With several bones selected, rotate every bone but the FIRST about the
    /// first one's frame, so each keeps the angle it held to it — Ktisis'
    /// <c>GizmoConfig.RelativeBones</c> (TransformTarget.cs:158-163), which it
    /// ships on with no way to turn off. Off is Brio's behaviour and Poser's
    /// own to date: one delta reaches every selected bone unchanged. It
    /// affects rotation only, and only a multi-bone selection.
    /// </summary>
    public bool RelativeSecondaryBones { get; set; } = false;

    /// <summary>
    /// Selecting a bone also selects its <c>_l</c>/<c>_r</c> counterpart, for
    /// the whole session — Ktisis' <c>EditorConfig.PersistentSiblingLink</c>
    /// (SelectManager.cs:209-223), a MODE rather than the one-shot "Select
    /// mirrored bone" command Poser already has. It also arms
    /// <c>IBonePosingService.LinkedBonesEnabled</c>, the same-delta catalog
    /// (both eyes, the Viera ear-variant chains) whose partners are not
    /// <c>_l</c>/<c>_r</c> pairs and so cannot be reached by co-selection.
    /// Off by default, which is the behaviour Poser has always had.
    /// </summary>
    public bool LinkSiblingBones { get; set; } = false;

    /// <summary>
    /// The toolbar's Off | Link | Mirror remembers PER BONE: with this on,
    /// clicking the toolbar while bones are selected states those bones'
    /// own mode (clicking their stated value again clears it), and a bone
    /// with no stated mode follows the toolbar as ever. Off is the
    /// behaviour Poser has always had: one global mode.
    /// </summary>
    public bool PerBoneSymmetry { get; set; } = false;

    /// <summary>
    /// Paired bones — the eyes and the Viera ear groups, the same trusted
    /// catalog the link expansion uses — default to LINK without being
    /// stated, unless the user states them otherwise. An option, as
    /// ruled; the explicit per-bone statement always outranks it.
    /// </summary>
    public bool AutoLinkPairedBones { get; set; } = true;

    /// <summary>The stated per-bone modes, by canonical bone name — only
    /// the bones the user explicitly set.</summary>
    public System.Collections.Generic.Dictionary<string, Poser.Services.SymmetryMode>
        BoneSymmetryOverrides { get; set; } = new();

    /// <summary>
    /// How many edits the undo history keeps, read live on every recorded edit
    /// (Brio's <c>Posing.UndoStackSize</c>, same zero-means-off semantics —
    /// <c>HistoryService.cs:17-24</c>). Poser's own long-standing depth is the
    /// default, not Brio's 50. Kept in step with
    /// <c>TransformHistory.DefaultCapacity</c>, which this assembly cannot
    /// reference (config sits below the application layer).
    /// </summary>
    public int UndoDepth { get; set; } = 500;

    /// <summary>
    /// Freeze every actor the spawn browser adds to the scene the moment it
    /// binds — Brio's <c>SpawnEx(spawnFrozen)</c>, which waits for the actor to
    /// be ready and then writes an overall animation speed of zero
    /// (<c>Brio/IPC/API/ActorAPI.cs:87-95</c>). The spawn browser's own toggle
    /// is what writes this, so the persisted value IS that toggle's state.
    /// </summary>
    public bool SpawnFrozen { get; set; } = false;

    /// <summary>Where spawned entries land by default — the rule every
    /// saved thing obeys. In front of the camera unless the user says
    /// otherwise.</summary>
    public Poser.Files.ObjectPlacementMode DefaultSpawnPlacement { get; set; }
        = Poser.Files.ObjectPlacementMode.InFrontOfCamera;

    /// <summary>
    /// The revision of the first-run notice this config has accepted (see
    /// <see cref="FirstRunNotice"/>). Zero — the value every config written
    /// before the notice existed deserialises to — means it has never been
    /// accepted, so the gate shows. This is deliberately NOT a migration
    /// step: an existing user has not read the notice either.
    /// </summary>
    public int AcceptedNoticeVersion { get; set; }
}
