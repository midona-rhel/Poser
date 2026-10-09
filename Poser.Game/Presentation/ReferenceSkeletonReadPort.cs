using System.Numerics;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Game.Bindings;

namespace Poser.Game.Presentation;

public sealed class ReferenceSkeletonReadPort(StableBindingRegistry bindings) : IReferenceSkeletonReadPort
{
    public IReadOnlyDictionary<BoneId, Vector3> Read(SkeletonId skeleton)
    {
        var positions = new Dictionary<BoneId, Vector3>();
        if (bindings.ResolveSkeleton(skeleton) is not { } resolved) return positions;
        // Capture composes bind-local transforms without applying them to the live actor.
        foreach (var (bone, reference) in resolved.CaptureReferencePose())
            if (bindings.GetBoneId(bone) is { } id && id.Skeleton == skeleton
                && float.IsFinite(reference.Position.X) && float.IsFinite(reference.Position.Y) && float.IsFinite(reference.Position.Z))
                positions[id] = reference.Position;
        return positions;
    }
}
