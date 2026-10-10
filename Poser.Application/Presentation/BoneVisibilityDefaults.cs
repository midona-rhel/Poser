using Poser.Config;
using Poser.Domain.Identity;
using Poser.Documents.Config;

namespace Poser.Application.Presentation;

public static class BoneVisibilityDefaults
{
    // Filter the requested scope, never introduce bones from another actor or slot.
    public static IReadOnlyList<BoneId> Resolve(SkeletonConfiguration settings, IReadOnlyList<BoneId> scope)
    {
        var names = settings.BoneVisibilityPresets.Where(p => p.ShowByDefault)
            .SelectMany(p => p.Bones).ToHashSet(StringComparer.Ordinal);
        return scope.Where(bone => names.Contains(bone.CanonicalName)).ToArray();
    }

    public static bool CanApply(SkeletonConfiguration settings, IReadOnlyList<BoneId> scope)
    {
        foreach (var preset in settings.BoneVisibilityPresets)
            if (preset.ShowByDefault)
                foreach (var bone in scope)
                    if (preset.Bones.Contains(bone.CanonicalName)) return true;
        return false;
    }
}
