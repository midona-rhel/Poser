using System;
using System.Numerics;
using Poser.Core;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for solving inverse kinematics on bone chains.
/// </summary>
public interface IIKService : IDisposable
{
    /// <summary>
    /// Solves IK for a bone chain to reach a target position.
    /// The havok pose is resolved internally from the bone's skeleton.
    /// </summary>
    /// <param name="bone">The end bone of the IK chain.</param>
    /// <param name="target">Target position in model space.</param>
    /// <param name="ikInfo">IK configuration.</param>
    void SolveIK(IBone bone, Vector3 target, BoneIKInfo ikInfo);
}
