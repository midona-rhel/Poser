using System.Collections.Generic;
using System;
using Poser.Domain.Posing.BoneInfo.Categories;

namespace Poser.Domain.Posing.BoneInfo;

/// <summary>
/// Central service for bone information: curated translations and the
/// NSFW (IVCS/extended) set.
/// Aggregates data from all category files.
/// </summary>
public static class BoneInfoService
{
    private static Action<string>? _log;
    private static readonly HashSet<string> _loggedUntranslated = new();

    /// <summary>
    /// Untranslated names waiting to be logged together, avoiding a separate log write for every bone.
    /// </summary>
    private static readonly List<string> _pendingUntranslated = new();

    /// <summary>How many names one flushed line spells out before it
    /// summarises the remainder as a count.</summary>
    private const int MaxListedUntranslated = 40;

    private static readonly Dictionary<string, BoneData> _boneData = new();
    private static readonly HashSet<string> _nsfwBones = new();

    /// <summary>
    /// Initializes the bone info service with a logger.
    /// </summary>
    public static void Initialize(Action<string> log)
    {
        _log = log;
        lock (_loggedUntranslated)
        {
            _loggedUntranslated.Clear();
            _pendingUntranslated.Clear();
        }
        _boneData.Clear();
        _nsfwBones.Clear();

        // Register all category data
        RootBones.Register(_boneData);
        SpineBones.Register(_boneData);
        ChestBones.Register(_boneData);
        HeadBones.Register(_boneData);
        FaceBones.Register(_boneData);
        HairBones.Register(_boneData);
        EarBones.Register(_boneData);
        LeftArmBones.Register(_boneData);
        RightArmBones.Register(_boneData);
        LeftHandBones.Register(_boneData);
        RightHandBones.Register(_boneData);
        LeftLegBones.Register(_boneData);
        RightLegBones.Register(_boneData);
        TailBones.Register(_boneData);
        ClothingBones.Register(_boneData);
        EquipmentBones.Register(_boneData);
        // IVCS and physics registrations double as the NSFW set: the
        // Display.ShowNsfwBones switch ("Show IVCS and extended bone
        // groups") controls exactly the bones these two files declare.
        var extended = new Dictionary<string, BoneData>();
        IVCSBones.Register(extended);
        PhysicsBones.Register(extended);
        foreach (var (name, boneData) in extended)
        {
            _boneData[name] = boneData;
            _nsfwBones.Add(name);
        }

        log.Invoke($"[BoneInfoService] Loaded {_boneData.Count} bone definitions");
    }

    /// <summary>True for IVCS/extended-group bones, the set the
    /// Display.ShowNsfwBones switch shows and hides.</summary>
    public static bool IsNsfw(string boneName) => _nsfwBones.Contains(boneName);


    /// <summary>
    /// Gets the curated data for a bone.
    /// </summary>
    public static BoneData? GetBoneData(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
            return data;
        return null;
    }

    /// <summary>
    /// Returns the friendly bone name, or the game name if no translation exists.
    /// Untranslated names are queued for logging once after each Initialize call.
    /// </summary>
    public static string GetDisplayName(string boneName)
    {
        if (_boneData.TryGetValue(boneName, out var data))
        {
            return data.Translation;
        }

        // Name lookups can run on multiple threads. Protect both the deduplication
        // set and the pending batch with the same lock.
        lock (_loggedUntranslated)
        {
            if (_loggedUntranslated.Add(boneName))
                _pendingUntranslated.Add(boneName);
        }

        return boneName;
    }

    /// <summary>
    /// Logs one warning for newly encountered untranslated names, listing at most 40.
    /// Does nothing when there are no pending names or no logger.
    /// </summary>
    public static void FlushUntranslatedLog()
    {
        if (_log == null)
            return;

        string[] names;
        lock (_loggedUntranslated)
        {
            if (_pendingUntranslated.Count == 0)
                return;
            names = _pendingUntranslated.ToArray();
            _pendingUntranslated.Clear();
        }

        var listed = names.Length <= MaxListedUntranslated
            ? names
            : names[..MaxListedUntranslated];
        var overflow = names.Length - listed.Length;
        var suffix = overflow > 0 ? $" (+{overflow} more)" : string.Empty;
        _log.Invoke(
            $"[BoneInfo] {names.Length} untranslated bone(s): {string.Join(", ", listed)}{suffix}");
    }
}
