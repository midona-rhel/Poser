using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>
/// One IK chain on a skeleton: its end bone, its settings, and the names of
/// the bones it moves. Views that show every chain at once, such as the
/// overlay colouring the bones of switched-on chains, read this list instead
/// of asking about each bone in turn.
/// </summary>
public readonly record struct IkChainSummary(
    BoneId Endpoint,
    IkChainConfig Config,
    IReadOnlyList<string> Bones);

/// <summary>
/// Reads and writes the IK settings of one chain, named by its end bone.
/// </summary>
public interface IIkConfigurationPort
{
    /// <summary>Whether the bone can be the end of an IK chain.</summary>
    bool IsSupported(TransformTargetId target);

    /// <summary>Every chain set up on the skeleton, switched on or not.</summary>
    IReadOnlyList<IkChainSummary> Chains(SkeletonId skeleton);

    /// <summary>Whether the skeleton has every bone the Two Joint solver needs
    /// above this end bone. Two Joint is offered only when it does.</summary>
    bool IsTwoJointAvailable(TransformTargetId target);

    /// <summary>The chain's current settings, or its default settings if none
    /// were saved. Null when the bone cannot be the end of a chain.</summary>
    IkChainConfig? Get(TransformTargetId target);

    /// <summary>Checks and saves the settings as one undo step. Switching the
    /// target to World, or switching on a chain whose target is World, pins
    /// the target where the end bone is now.</summary>
    Outcome Set(TransformTargetId target, IkChainConfig config);
    Outcome Adjust(TransformTargetId target, IkChainConfig config);
    Outcome SetFabrikTarget(TransformTargetId target, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null) => Outcome.Fail("FABRIK targets unavailable.");

    /// <summary>Restores the chain's defaults while preserving its current
    /// Enabled state.</summary>
    Outcome ResetDefaults(TransformTargetId target);

    /// <summary>Makes the end bone follow another bone (Target: Bone). The end
    /// bone keeps its current distance from that bone.</summary>
    Outcome SetBoneTarget(
        TransformTargetId target, global::Poser.Domain.Identity.BoneId bone);

    /// <summary>The bone a Bone-mode chain follows, if one was picked.</summary>
    global::Poser.Domain.Identity.BoneId? BoneTarget(TransformTargetId target);

    /// <summary>Makes the end bone follow something in the scene that is not a
    /// bone (Target: Scene entity), keeping its current distance from it.</summary>
    Outcome SetEntityTarget(TransformTargetId target, SelectionId entity);
    SelectionId? EntityTarget(TransformTargetId target);
}
