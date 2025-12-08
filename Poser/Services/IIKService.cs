using System;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
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
    /// </summary>
    /// <param name="pose">The havok pose to modify.</param>
    /// <param name="ikInfo">IK configuration.</param>
    /// <param name="bone">The end bone of the IK chain.</param>
    /// <param name="target">Target position in model space.</param>
    unsafe void SolveIK(hkaPose* pose, BoneIKInfo ikInfo, IBone bone, Vector3 target);
}
