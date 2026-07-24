using System;
using System.Numerics;

namespace Poser.Core;

/// <summary>
/// Configuration for IK solving on a bone.
/// </summary>
public struct BoneIKInfo
{
    /// <summary>
    /// Whether IK is enabled for this bone.
    /// </summary>
    public bool Enabled = false;

    /// <summary>
    /// Whether to enforce IK constraints (if false, manual position overrides IK result).
    /// </summary>
    public bool EnforceConstraints = true;

    /// <summary>
    /// The type of IK solver to use.
    /// </summary>
    public IKSolverType SolverType = IKSolverType.CCD;

    /// <summary>
    /// Options for CCD (Cyclic Coordinate Descent) solver.
    /// </summary>
    public CCDOptions CCD = new();

    /// <summary>
    /// Options for Two-Joint IK solver (used for limbs).
    /// </summary>
    public TwoJointOptions TwoJoint = new();

    /// <summary>
    /// A disabled IK configuration.
    /// </summary>
    public static readonly BoneIKInfo Disabled = new() { Enabled = false };

    public BoneIKInfo() { }

    /// <summary>
    /// Determines if a bone can use Two-Joint IK (arms or legs).
    /// </summary>
    public static bool CanUseJoint(string boneName) =>
        boneName.StartsWith("j_te") || boneName.StartsWith("j_asi_d");

    /// <summary>
    /// The supported IK chain ends: the four bones with authored solver
    /// setups (hands and feet). Every retained arming path — the Live IK
    /// translate gesture and the bulk arm action — is limited to this set;
    /// no UI path arms any other bone.
    /// </summary>
    public static readonly string[] SupportedChainEnds =
        { "j_te_l", "j_te_r", "j_asi_d_l", "j_asi_d_r" };

    /// <summary>Whether the bone is one of the supported chain ends.</summary>
    public static bool IsSupportedChainEnd(string boneName) =>
        Array.IndexOf(SupportedChainEnds, boneName) >= 0;

    /// <summary>
    /// Calculates the default IK configuration for a bone based on its name.
    /// </summary>
    public static BoneIKInfo CalculateDefault(string boneName, bool allowJoint = true)
    {
        var result = new BoneIKInfo();

        if (allowJoint && CanUseJoint(boneName))
        {
            if (boneName.StartsWith("j_te"))
            {
                // Arms - use Two-Joint IK
                result.SolverType = IKSolverType.TwoJoint;
                result.TwoJoint = new TwoJointOptions
                {
                    FirstBone = 2,
                    SecondBone = 1,
                    EndBone = 0,
                    RotationAxis = Vector3.UnitZ
                };
            }
            else if (boneName.StartsWith("j_asi_d"))
            {
                // Legs - use Two-Joint IK
                result.SolverType = IKSolverType.TwoJoint;
                result.TwoJoint = new TwoJointOptions
                {
                    FirstBone = 3,
                    SecondBone = 1,
                    EndBone = 0,
                    RotationAxis = -Vector3.UnitZ
                };
            }
        }
        // All other bones (including gaze bones) use CCD by default

        return result;
    }
}

/// <summary>
/// Type of IK solver to use.
/// </summary>
public enum IKSolverType
{
    /// <summary>
    /// Cyclic Coordinate Descent - general purpose, works on any bone chain.
    /// </summary>
    CCD,

    /// <summary>
    /// Two-Joint solver - optimized for limbs (arms, legs).
    /// </summary>
    TwoJoint
}

/// <summary>
/// Options for CCD (Cyclic Coordinate Descent) IK solver.
/// </summary>
public struct CCDOptions
{
    /// <summary>
    /// How many parent bones to include in the IK chain.
    /// </summary>
    public int Depth = 3;

    /// <summary>
    /// Number of solver iterations.
    /// </summary>
    public int Iterations = 8;

    public CCDOptions() { }
}

/// <summary>
/// Options for Two-Joint IK solver.
/// </summary>
public struct TwoJointOptions
{
    /// <summary>
    /// Index of the first joint in the bone chain (counting from end bone).
    /// </summary>
    public int FirstBone = -1;

    /// <summary>
    /// Index of the second joint in the bone chain.
    /// </summary>
    public int SecondBone = -1;

    /// <summary>
    /// Index of the end bone.
    /// </summary>
    public int EndBone = -1;

    /// <summary>
    /// The hinge axis for joint rotation.
    /// </summary>
    public Vector3 RotationAxis = Vector3.Zero;

    public TwoJointOptions() { }
}
