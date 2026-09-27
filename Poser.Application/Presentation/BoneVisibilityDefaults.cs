using Poser.Config;
using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

public static class BoneVisibilityDefaults
{
    // Filter the requested scope, never introduce bones from another actor or slot.
    public static IReadOnlyList<BoneId> Resolve(SkeletonConfiguration settings, IReadOnlyList<BoneId> scope)
    {
        if (!settings.UseDefaultBonePresetsOnShow) return scope;
        var names = settings.BoneVisibilityPresets.Where(p => p.ShowByDefault)
            .SelectMany(p => p.Bones).ToHashSet(StringComparer.Ordinal);
        return scope.Where(bone => names.Contains(bone.CanonicalName)).ToArray();
    }
}
