using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Documents.Config;

namespace Poser.UI;

/// <summary>
/// Named bone-visibility sets applied per actor.
///
/// <para>Applied state is derived from
/// <see cref="SkeletonOverlayPresentation"/>: a preset is active when every
/// listed bone that exists on the actor is visible. This service does not keep
/// a second visibility-state map.</para>
/// </summary>
public sealed class BoneVisibilityPresetService
{
    private readonly SkeletonOverlayPresentation _presentation;
    private readonly Func<PoserConfiguration> _config;
    private readonly Action _save;

    public BoneVisibilityPresetService(
        SkeletonOverlayPresentation presentation,
        Func<PoserConfiguration> config,
        Action save)
    {
        _presentation = presentation;
        _config = config;
        _save = save;
        SeedDefaults();
    }

    /// <summary>Refreshes stock presets by name while preserving custom
    /// presets.</summary>
    private void SeedDefaults()
    {
        var skeleton = _config().Skeleton;
        if (skeleton.DefaultBonePresetsVersion >= DefaultBonePresets.Version)
            return;
        var store = skeleton.BoneVisibilityPresets;
        foreach (var stock in DefaultBonePresets.Build())
        {
            int at = store.FindIndex(preset =>
                string.Equals(preset.Name, stock.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                stock.ShowByDefault = store[at].ShowByDefault;
                store[at] = stock;
            }
            else
                store.Add(stock);
        }
        skeleton.DefaultBonePresetsVersion = DefaultBonePresets.Version;
        _save();
    }

    private List<BoneVisibilityPreset> Store =>
        _config().Skeleton.BoneVisibilityPresets;

    public IReadOnlyList<BoneVisibilityPreset> Presets => Store;

    public bool CanApplyDefaults(IReadOnlyList<BoneId> bones) =>
        Application.Presentation.BoneVisibilityDefaults.CanApply(_config().Skeleton, bones);

    public void ApplyDefaults(IReadOnlyList<BoneId> bones)
    {
        var wanted = Application.Presentation.BoneVisibilityDefaults.Resolve(_config().Skeleton, bones);
        if (wanted.Count == 0) return;
        _presentation.SetVisible(bones, false);
        _presentation.SetVisible(wanted, true);
    }

    /// <summary>Whether every bone of the preset that this actor actually
    /// carries is shown. A preset none of whose bones exist here is off — an
    /// empty match must not read as satisfied.</summary>
    public bool IsApplied(ActorDescriptor actor, string name)
    {
        var bones = Match(actor, name);
        return bones.Count > 0 && _presentation.AreVisible(bones);
    }

    /// <summary>Applies or lifts the preset on this actor; omitting
    /// <paramref name="on"/> flips whatever it currently reads as.</summary>
    public void Toggle(ActorDescriptor actor, string name, bool? on = null)
    {
        var bones = Match(actor, name);
        if (bones.Count == 0)
            return;
        _presentation.SetVisible(bones, on ?? !_presentation.AreVisible(bones));
    }

    /// <summary>Shows bones absent from every preset and hides the rest.</summary>
    public void ToggleOther(ActorDescriptor actor)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in Store)
            foreach (var bone in preset.Bones)
                covered.Add(bone);

        var show = new List<BoneId>();
        var hide = new List<BoneId>();
        foreach (var bone in AllBones(actor))
            (covered.Contains(bone.CanonicalName) ? hide : show).Add(bone);

        _presentation.SetVisible(hide, false);
        _presentation.SetVisible(show, true);
    }

    /// <summary>Hides every bone this actor carries, on every slot.</summary>
    public void Clear(ActorDescriptor actor) =>
        _presentation.SetVisible(AllBones(actor).ToArray(), false);

    /// <summary>Stores what this actor currently shows under a new name.
    /// Returns the refusal reason, or null when it was stored.</summary>
    public string? SaveCurrent(string name, ActorDescriptor actor)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Name the preset first.";
        if (Find(trimmed) != null)
            return $"A preset called '{trimmed}' already exists.";

        var bones = AllBones(actor)
            .Where(bone => _presentation.IsVisible(bone))
            .Select(bone => bone.CanonicalName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(bone => bone, StringComparer.Ordinal)
            .ToList();
        if (bones.Count == 0)
            return "Nothing is shown on this actor to save.";

        Store.Add(new BoneVisibilityPreset { Name = trimmed, Bones = bones });
        Sort();
        _save();
        return null;
    }

    public bool Delete(string name)
    {
        if (Find(name) is not { } preset)
            return false;
        Store.Remove(preset);
        _save();
        return true;
    }

    private BoneVisibilityPreset? Find(string name)
    {
        foreach (var preset in Store)
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
                return preset;
        return null;
    }

    private void Sort() =>
        Store.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset's bones as they exist on THIS actor, across every
    /// slot skeleton it carries.</summary>
    private IReadOnlyList<BoneId> Match(ActorDescriptor actor, string name)
    {
        if (Find(name) is not { } preset)
            return Array.Empty<BoneId>();
        var wanted = new HashSet<string>(preset.Bones, StringComparer.Ordinal);
        var matched = new List<BoneId>();
        foreach (var bone in AllBones(actor))
            if (wanted.Contains(bone.CanonicalName))
                matched.Add(bone);
        return matched;
    }

    private static IEnumerable<BoneId> AllBones(ActorDescriptor actor)
    {
        foreach (var skeleton in actor.Skeletons)
            foreach (var bone in skeleton.Bones)
                yield return bone.Id;
    }
}
