using System.Numerics;
using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>Read-only model-space reference joints for a specific skeleton generation.</summary>
public interface IReferenceSkeletonReadPort
{
    IReadOnlyDictionary<BoneId, Vector3> Read(SkeletonId skeleton);
}
