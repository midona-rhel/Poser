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
    public const int LatestVersion = 3;

    public SkeletonConfiguration Skeleton { get; set; } = new();
    public GizmoConfiguration Gizmo { get; set; } = new();
    public DisplayConfiguration Display { get; set; } = new();
    public UIConfiguration UI { get; set; } = new();
    public IntegrationConfiguration Integration { get; set; } = new();
    public LibraryConfiguration Library { get; set; } = new();
    public AutoSaveConfiguration AutoSave { get; set; } = new();

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
    /// Park the authored pose while a slot skeleton is rebuilt and restore it
    /// onto the replacement (Ktisis semantics: rotation everywhere, position on
    /// the pose root only). Off means a redraw keeps the current behaviour of
    /// giving the replacement a fresh, empty pose store.
    /// </summary>
    public bool PreservePoseAcrossRedraws { get; set; } = true;

    /// <summary>
    /// How many edits the undo history keeps, read live on every recorded edit
    /// (Brio's <c>Posing.UndoStackSize</c>, same zero-means-off semantics —
    /// <c>HistoryService.cs:17-24</c>). Poser's own long-standing depth is the
    /// default, not Brio's 50. Kept in step with
    /// <c>TransformHistory.DefaultCapacity</c>, which this assembly cannot
    /// reference (config sits below the application layer).
    /// </summary>
    public int UndoDepth { get; set; } = 200;

    /// <summary>
    /// Freeze every actor the spawn browser adds to the scene the moment it
    /// binds — Brio's <c>SpawnEx(spawnFrozen)</c>, which waits for the actor to
    /// be ready and then writes an overall animation speed of zero
    /// (<c>Brio/IPC/API/ActorAPI.cs:87-95</c>). The spawn browser's own toggle
    /// is what writes this, so the persisted value IS that toggle's state.
    /// </summary>
    public bool SpawnFrozen { get; set; } = false;

    /// <summary>
    /// The revision of the first-run notice this config has accepted (see
    /// <see cref="FirstRunNotice"/>). Zero — the value every config written
    /// before the notice existed deserialises to — means it has never been
    /// accepted, so the gate shows. This is deliberately NOT a migration
    /// step: an existing user has not read the notice either.
    /// </summary>
    public int AcceptedNoticeVersion { get; set; }
}
