using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binder for <see cref="PoseLibraryView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): owns the rail, the tile list, the filter
/// cache, the footer caption and every apply/spawn call the grid makes.
///
/// <para>The scan lives in <see cref="IPoseLibraryService"/> and publishes one
/// immutable snapshot; this rebuilds its rows only when the revision moves, so
/// a warm frame reads rows it minted at scan time and allocates nothing.</para>
///
/// <para>The library is a MODE of the shell workspace rather than a window, so
/// <see cref="Tick"/> runs every frame whether or not the mode is showing: a
/// spawn started here has to complete even after the user has gone back to an
/// actor.</para>
/// </summary>
public sealed class PoseLibraryPane
{
    /// <summary>Double-click applies, and the action row's primary applies the
    /// same tile: a second apply of the SAME tile is swallowed rather than
    /// importing twice.</summary>
    private const double ReactivationSwallow = 0.35;

    /// <summary>A spawned actor binds on a later scene refresh. Past this many
    /// frames it never will, and the queued pose is dropped rather than held
    /// forever.</summary>
    private const int PendingSpawnFrames = 120;

    private const string AllKey = "##pose-library-all";
    private const string FavoritesKey = "##pose-library-favorites";
    private const string AllLabel = "All poses";
    private const string FavoritesLabel = "Favorites";

    private readonly ConfigurationService _config;
    private readonly IPoseLibraryService _library;
    private readonly PoseThumbnailCache _thumbs;
    private readonly CleanPoseFacade _poseFacade;
    private readonly IActorSpawnService _spawnService;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly PoseLibraryViewModel _vm = new();

    /// <summary>The snapshot the rows were built from. Tiles are 1:1 with its
    /// entries in order, so an entry's search fields are reachable by tile
    /// index without copying them onto the row.</summary>
    private PoseLibrarySnapshot? _snapshot;
    private int _seenRevision = -1;

    private string _query = string.Empty;
    private string _queryLower = string.Empty;
    private string? _tagLower;
    private bool _refilter = true;

    /// <summary>The selected rail row's contiguous descendant span in VIEW row
    /// indices, or -1 when the selection is not a folder. The tree is flattened
    /// depth-first, so a subtree is exactly the rows up to the next row at the
    /// same or shallower depth.</summary>
    private int _rangeStart = -1;
    private int _rangeEnd = -1;

    // The caption is a STRING PER COUNT, not per frame: it is rebuilt only when
    // the number it states or the mode it states it in changes.
    private string _caption = string.Empty;
    private int _captionCount = -1;
    private bool _captionFiltered;
    private bool _captionScanning;

    /// <summary>Why the last apply or spawn did nothing, or null. Cleared by
    /// the next one and by any filter change.</summary>
    private string? _note;

    private ActorId? _targetId;
    private string _targetName = string.Empty;

    private int _lastAppliedTile = -1;
    private double _lastAppliedAt;

    private bool _iconSizeDirty;

    /// <summary>Whether the pane is the workspace's current content. The first
    /// draw after it becomes true is the old window's OnOpen.</summary>
    private bool _showing;

    private IActor? _pendingActor;
    private string? _pendingPath;
    private PoseImportOptions? _pendingOptions;
    private int _pendingFrames;

    /// <summary>Raised by the no-sources empty state and by the action row's
    /// "Add source…"; the UI manager owns the settings window.</summary>
    public event Action? OnSettingsRequested;

    public PoseLibraryPane(
        ConfigurationService config,
        IPoseLibraryService library,
        PoseThumbnailCache thumbs,
        CleanPoseFacade poseFacade,
        IActorSpawnService spawnService,
        SelectionSession selection,
        StableBindingRegistry bindings,
        PoseFileInspectorSection poseFileSection)
    {
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _poseFacade = poseFacade;
        _spawnService = spawnService;
        _selection = selection;
        _bindings = bindings;
        _poseFileSection = poseFileSection;

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnSelectFolder = SelectFolder;
        _vm.OnSelect = Select;
        _vm.OnApplyTile = Apply;
        _vm.OnSpawnTile = Spawn;
        _vm.OnToggleFavorite = ToggleFavorite;
        _vm.OnTagFilter = TagFilter;
        _vm.OnIconSize = SetIconSize;
        _vm.OnRefresh = () => _library.RequestScan();
        _vm.OnOpenSettings = () => OnSettingsRequested?.Invoke();
        _vm.ResolveThumbnail = ResolveThumbnail;
        // Spawning needs no selection and no scene state; the service answers
        // null when the game refuses, which is a note rather than a gate.
        _vm.CanSpawn = true;
    }

    /// <summary>
    /// The frame work that outlives the mode: the thumbnail cache's decode
    /// pump, and a spawn waiting for the scene to bind its actor. Leaving the
    /// library must not strand a pose that was already asked for, so the shell
    /// calls this unconditionally.
    /// </summary>
    public void Tick()
    {
        _thumbs.Tick();
        ReconcilePendingSpawn();
    }

    /// <summary>Draws into the rect the shell hands the workspace.</summary>
    public void Draw(Vector2 origin, Vector2 size)
    {
        if (!_showing)
        {
            _showing = true;
            Enter();
        }

        SyncSnapshot();
        SyncQuery();
        if (_refilter)
            Refilter();
        SyncTarget();
        SyncStatus();

        PoseLibraryView.Draw(_vm, origin, size);
    }

    /// <summary>The workspace moved on. The decoded thumbnails are a cache of
    /// what was on screen, and the icon size is persisted here rather than on
    /// every drag tick.</summary>
    public void OnHidden()
    {
        _showing = false;
        _thumbs.Clear();
        if (!_iconSizeDirty)
            return;
        // The slider writes the config live so the grid reflows with the drag;
        // the disk write waits for the surface to close.
        _iconSizeDirty = false;
        _config.Save();
    }

    /// <summary>The first frame of a library session.</summary>
    private void Enter()
    {
        // Nothing is scanned until a surface asks, so the first entry is what
        // pays for the file system.
        _library.RequestScan();

        // The query and the tag are DRAFTS: they mean nothing outside the open
        // surface, so each entry starts on the whole library.
        _vm.Query = string.Empty;
        _query = string.Empty;
        _queryLower = string.Empty;
        _vm.ActiveTag = null;
        _tagLower = null;
        _note = null;
        _lastAppliedTile = -1;
        _vm.IconSize = _config.Config.Library.IconSize;
        _iconSizeDirty = false;

        // Favourites and sources may have moved while the mode was away, and a
        // completed scan keeps its revision: rebuild unconditionally.
        _seenRevision = -1;
        _refilter = true;
    }

    // ── the rows ─────────────────────────────────────────────────────────

    /// <summary>Rebuilds the rail and the tiles from a new snapshot. Every
    /// string a frame reads is minted HERE, the count readouts included.
    /// </summary>
    private void SyncSnapshot()
    {
        var snapshot = _library.Snapshot;
        if (snapshot.Revision == _seenRevision)
            return;
        _seenRevision = snapshot.Revision;
        _snapshot = snapshot;

        var favorites = _config.Config.Library.Favorites;
        var entries = snapshot.Entries;

        int favored = 0;
        for (int i = 0; i < entries.Count; i++)
            if (favorites.Contains(entries[i].FilePath))
                favored++;

        // The two synthetic heads are positional by contract: [0] "All poses",
        // [1] "Favorites", the scan's own folders after them. A tile therefore
        // states its rail row as its snapshot folder index + 2.
        var folders = _vm.Folders;
        folders.Clear();
        folders.Add(new PoseLibraryFolderRow
        {
            Key = AllKey,
            Label = AllLabel,
            LabelLower = "all poses",
            Depth = 0,
            Count = entries.Count,
            CountText = Count(entries.Count),
        });
        folders.Add(new PoseLibraryFolderRow
        {
            Key = FavoritesKey,
            Label = FavoritesLabel,
            LabelLower = "favorites",
            Depth = 0,
            Count = favored,
            CountText = Count(favored),
        });
        var scanned = snapshot.Folders;
        for (int i = 0; i < scanned.Count; i++)
        {
            var folder = scanned[i];
            folders.Add(new PoseLibraryFolderRow
            {
                Key = folder.Key,
                Label = folder.Label,
                LabelLower = folder.LabelLower,
                Depth = folder.Depth,
                Count = folder.Count,
                CountText = Count(folder.Count),
            });
        }

        var tiles = _vm.Tiles;
        tiles.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            tiles.Add(new PoseLibraryTileRow
            {
                Id = entry.FilePath,
                Label = entry.Name,
                LabelLower = entry.NameLower,
                Sub = entry.ModifiedText,
                ThumbKey = entry.FilePath,
                HasThumbnail = entry.HasThumbnail,
                Favorite = favorites.Contains(entry.FilePath),
                Legacy = entry.IsLegacy,
                Author = entry.Author,
                Tags = entry.Tags,
                Folder = entry.Folder,
            });
        }

        // Row identity did not survive the rebuild, so neither does the
        // selection; a rail row that no longer exists falls back to "All".
        _vm.Selected = -1;
        if (_vm.SelectedFolder >= folders.Count)
            _vm.SelectedFolder = 0;
        SyncFolderRange();
        _refilter = true;
    }

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private void SyncQuery()
    {
        if (string.Equals(_query, _vm.Query, StringComparison.Ordinal))
            return;
        _query = _vm.Query;
        // Lowercased ONCE per query change; the scan below compares ordinal
        // against names that were lowercased when the snapshot was built.
        _queryLower = _query.Trim().ToLowerInvariant();
        _note = null;
        _refilter = true;
    }

    /// <summary>The selected rail row's descendant span. Depth-first flattening
    /// makes a subtree contiguous, so a folder test is a range test rather than
    /// a walk.</summary>
    private void SyncFolderRange()
    {
        int selected = _vm.SelectedFolder;
        var folders = _vm.Folders;
        if (selected < 2 || selected >= folders.Count)
        {
            _rangeStart = -1;
            _rangeEnd = -1;
            return;
        }
        int depth = folders[selected].Depth;
        int end = folders.Count;
        for (int i = selected + 1; i < folders.Count; i++)
        {
            if (folders[i].Depth > depth)
                continue;
            end = i;
            break;
        }
        _rangeStart = selected;
        _rangeEnd = end;
    }

    /// <summary>
    /// The visible list, refilled in place. A query searches the WHOLE library
    /// and ignores the folder tree — a name is looked for, not a place — while
    /// Favorites and the tag chip are filters and compose with it.
    /// </summary>
    private void Refilter()
    {
        _refilter = false;
        var visible = _vm.Visible;
        var tiles = _vm.Tiles;
        var entries = _snapshot?.Entries;
        visible.Clear();

        bool query = _queryLower.Length > 0;
        int folder = _vm.SelectedFolder;
        int selected = _vm.Selected;
        bool kept = false;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (folder == 1 && !tile.Favorite)
                continue;
            if (query)
            {
                if (!tile.LabelLower.Contains(_queryLower, StringComparison.Ordinal))
                    continue;
            }
            else if (folder >= 2)
            {
                int row = tile.Folder + 2;
                if (row < _rangeStart || row >= _rangeEnd)
                    continue;
            }
            if (_tagLower is { Length: > 0 } tag
                && (entries is null || !HasTag(entries[i], tag)))
                continue;

            if (i == selected)
                kept = true;
            visible.Add(i);
        }

        // A selection the filter dropped is no longer on screen, so it stops
        // being what the action row would act on.
        if (!kept)
            _vm.Selected = -1;
    }

    private static bool HasTag(PoseLibraryEntry entry, string tagLower)
    {
        var tags = entry.TagsLower;
        for (int i = 0; i < tags.Count; i++)
            if (string.Equals(tags[i], tagLower, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void SyncStatus()
    {
        bool scanning = _library.IsScanning;
        _vm.IsScanning = scanning;

        if (_note is { } note)
        {
            _vm.Status = note;
            return;
        }

        bool filtered = _queryLower.Length > 0
            || _tagLower is { Length: > 0 }
            || _vm.SelectedFolder != 0;
        if (_captionCount != _vm.Visible.Count
            || _captionFiltered != filtered
            || _captionScanning != scanning)
        {
            _captionCount = _vm.Visible.Count;
            _captionFiltered = filtered;
            _captionScanning = scanning;
            _caption = scanning
                ? "Scanning…"
                : Count(_captionCount) + (filtered ? " matches" : " poses");
        }
        _vm.Status = _caption;
    }

    // ── the target actor ─────────────────────────────────────────────────

    /// <summary>The apply target: the selection's actor — a bone selection
    /// resolves to the actor that owns it — as a live actor, or null when
    /// nothing resolves. Entering the library does not clear the selection, so
    /// the actor being posed is still the actor a pose lands on.</summary>
    private IActor? TargetActor()
    {
        var actorId = _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
                bone.Skeleton.Actor,
            _ => (ActorId?)null,
        };
        if (actorId is not { } id)
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>The primary action's caption, minted only when the actor it
    /// names changes — the same discipline as the footer count.</summary>
    private void SyncTarget()
    {
        var actor = TargetActor();
        // A pose is applied to a skeleton; an actor without one is not a
        // target, exactly as the actor menu's own import action states.
        bool can = actor is { HasSkeleton: true };
        _vm.CanApply = can;

        var id = can ? _bindings.GetActorId(actor!) : null;
        string name = can ? actor!.Name : string.Empty;
        if (Nullable.Equals(id, _targetId)
            && string.Equals(name, _targetName, StringComparison.Ordinal))
            return;
        _targetId = id;
        _targetName = name;
        _vm.ApplyLabel = id is { } value
            ? "Apply to " + _config.GetDisplayName(value.LogicalId, Clean(name))
            : "Apply";
    }

    /// <summary>Strips the raw object-index suffix ("Name (201)") the scene
    /// names carry, matching what every other surface displays.</summary>
    private static string Clean(string name)
    {
        int open = name.LastIndexOf('(');
        if (open <= 0 || name[^1] != ')')
            return name;
        for (int i = open + 1; i < name.Length - 1; i++)
            if (name[i] is < '0' or > '9')
                return name;
        return name.AsSpan(0, open).TrimEnd().ToString();
    }

    // ── the grid's actions ───────────────────────────────────────────────

    private void Select(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        _vm.Selected = index;
        _note = null;
    }

    private void SelectFolder(int index)
    {
        if (index < 0 || index >= _vm.Folders.Count
            || index == _vm.SelectedFolder)
            return;
        _vm.SelectedFolder = index;
        _note = null;
        SyncFolderRange();
        _refilter = true;
    }

    private void TagFilter(string? tag)
    {
        _vm.ActiveTag = tag;
        // Lowercased ONCE per change; the scan compares ordinal against tags
        // the snapshot already lowercased.
        _tagLower = tag?.ToLowerInvariant();
        _note = null;
        _refilter = true;
    }

    private void SetIconSize(float size)
    {
        _vm.IconSize = size;
        int rounded = (int)MathF.Round(size);
        if (_config.Config.Library.IconSize == rounded)
            return;
        _config.Config.Library.IconSize = rounded;
        _iconSizeDirty = true;
    }

    private void ToggleFavorite(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var tile = _vm.Tiles[index];
        var favorites = _config.Config.Library.Favorites;
        bool favorite = !tile.Favorite;
        if (favorite)
            favorites.Add(tile.ThumbKey);
        else
            favorites.Remove(tile.ThumbKey);
        tile.Favorite = favorite;

        // Favouriting is a deliberate act with no other write to ride on, so
        // it persists immediately.
        _config.Save();

        var row = _vm.Folders.Count > 1 ? _vm.Folders[1] : null;
        if (row is not null)
        {
            row.Count += favorite ? 1 : -1;
            row.CountText = Count(row.Count);
        }
        if (_vm.SelectedFolder == 1)
            _refilter = true;
    }

    private void Apply(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        double now = ImGui.GetTime();
        if (index == _lastAppliedTile && now - _lastAppliedAt < ReactivationSwallow)
            return;
        _lastAppliedTile = index;
        _lastAppliedAt = now;

        if (TargetActor() is not { HasSkeleton: true } actor)
        {
            _note = "Select an actor to apply a pose to.";
            return;
        }
        var result = _poseFacade.ImportPose(
            actor,
            _vm.Tiles[index].ThumbKey,
            _poseFileSection.BuildImportOptions(),
            _poseFileSection.FreezeSelectedScope());
        _note = result.Success ? null : Failure(result);
    }

    private void Spawn(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var spawned = _spawnService.SpawnNewActor(reserveCompanionSlot: false);
        if (spawned is null)
        {
            _note = "The actor could not be spawned.";
            return;
        }

        // The options are frozen HERE, at the click, so a scope change made
        // while the scene binds the new actor cannot retarget the import. The
        // Selected-scope bone freeze is deliberately NOT taken: those BoneIds
        // belong to the source actor and the facade would reject them on the
        // spawn, so a spawn always applies the file without a bone filter.
        _pendingActor = spawned;
        _pendingPath = _vm.Tiles[index].ThumbKey;
        _pendingOptions = _poseFileSection.BuildImportOptions();
        _pendingFrames = 0;
        _note = null;
    }

    /// <summary>Second half of <see cref="Spawn"/>: the scene has not rescanned
    /// at click time, so the new actor is selected and posed once the refresh
    /// has bound it. The pending state is cleared BEFORE the import, so no
    /// outcome can apply the same pose twice.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingActor is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
        {
            if (++_pendingFrames < PendingSpawnFrames)
                return;
            ClearPendingSpawn();
            _note = "Spawned actor never became ready.";
            return;
        }

        var path = _pendingPath!;
        var options = _pendingOptions!;
        ClearPendingSpawn();

        _selection.Select(SelectionId.ForActor(id));
        var result = _poseFacade.ImportPose(spawned, path, options);
        _note = result.Success ? null : Failure(result);
    }

    private void ClearPendingSpawn()
    {
        _pendingActor = null;
        _pendingPath = null;
        _pendingOptions = null;
        _pendingFrames = 0;
    }

    private static string Failure(PoseEditResult result) =>
        "Apply: " + (result.Detail ?? "the pose could not be applied.");

    /// <summary>Resolves a tile's thumbnail. The view asks only for tiles that
    /// carry one, and a shared wrap must be re-resolved each frame, so this is
    /// a dictionary hit and nothing more.</summary>
    private PoseThumbnail ResolveThumbnail(string path)
    {
        var handle = _thumbs.Get(path, out var size);
        return handle == 0 ? default : new PoseThumbnail(handle, size);
    }
}
