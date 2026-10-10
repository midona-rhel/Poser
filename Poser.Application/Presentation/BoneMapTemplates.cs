using Poser.Config;
using Poser.Documents.Config;

namespace Poser.Application.Presentation;

public sealed record BoneMapTemplate(Guid Id, BoneMapKind Kind, string Name, string Section);

public static class BoneMapTemplates
{
    // Stable template identities are configuration references, never actor-instance identities.
    public static readonly IReadOnlyList<BoneMapTemplate> All = new[]
    {
        Entry(1, BoneMapKind.Body, "Default Body", "body"),
        Entry(2, BoneMapKind.Face, "Default Human", "human_head"),
        Entry(3, BoneMapKind.Face, "Default Miqo’te", "miqote_head"),
        Entry(4, BoneMapKind.Face, "Default Hrothgar", "hrothgar_head"),
        Entry(5, BoneMapKind.Face, "Default Viera A", "viera_head_a"),
        Entry(6, BoneMapKind.Face, "Default Viera B", "viera_head_b"),
        Entry(7, BoneMapKind.Face, "Default Viera C", "viera_head_c"),
        Entry(8, BoneMapKind.Face, "Default Viera D", "viera_head_d"),
    };

    private static BoneMapTemplate Entry(int id, BoneMapKind kind, string name, string section) =>
        new(new Guid($"88d6de19-3a1d-4f70-b22c-{id:000000000000}"), kind, name, section);

    public static bool IsBuiltIn(Guid id) => All.Any(item => item.Id == id);

    public static string DefaultFaceSection(byte race) => race switch
    {
        4 => "miqote_head",
        7 => "hrothgar_head",
        8 => "viera_head_a",
        _ => "human_head",
    };

    public static Guid ResolveDefault(SkeletonConfiguration config, BoneMapKind kind,
        byte race, byte gender, string headSection)
    {
        var rule = config.BoneMapDefaults.LastOrDefault(item => item.Kind == kind && item.Race == race && item.Gender == gender);
        if (rule != null && (All.Any(item => item.Id == rule.PresetId && item.Kind == kind)
            || config.BoneMapPresets.Any(item => item.Id == rule.PresetId && item.Kind == kind))) return rule.PresetId;
        return All.FirstOrDefault(item => item.Kind == kind && item.Section == (kind == BoneMapKind.Body ? "body" : headSection))?.Id
            ?? All.First(item => item.Kind == kind).Id;
    }
}
