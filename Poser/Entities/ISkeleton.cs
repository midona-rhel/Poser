using System;
using System.Collections.Generic;

namespace Poser.Entities;

/// <summary>
/// Represents a skeleton attached to an actor.
/// </summary>
public interface ISkeleton : IEntity
{
    /// <summary>
    /// The actor this skeleton belongs to.
    /// </summary>
    IActor Actor { get; }

    /// <summary>
    /// The root bone of the skeleton.
    /// </summary>
    IBone? RootBone { get; }

    /// <summary>
    /// All bones in the skeleton.
    /// </summary>
    IReadOnlyList<IBone> Bones { get; }

    /// <summary>
    /// Whether this skeleton is valid and has been initialized.
    /// </summary>
    bool IsValid { get; }

    /// <summary>
    /// Gets a bone by name.
    /// </summary>
    IBone? GetBone(string name);

    /// <summary>
    /// Gets a bone by index and partial ID.
    /// </summary>
    IBone? GetBone(int partialId, int boneIndex);

    /// <summary>
    /// Refreshes the skeleton data from game memory.
    /// </summary>
    void Refresh();
}
