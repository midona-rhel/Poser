using System;
using System.Numerics;
using Poser;
using Poser.Entities;

namespace Poser.Tests.Entities;

/// <summary>
/// The parent∘local model-space composition behind
/// <c>Skeleton.CaptureReferencePose</c> — the managed equivalent of what
/// hkaPose::SyncModelSpace does after SetToReferencePose. Reference locals
/// carry unit-ish scale, so S·R·T matrix composition and havok's QsTransform
/// multiply agree on every case here.
/// </summary>
public class ReferencePoseComposeTests
{
    private const float Tolerance = 1e-4f;

    private static void AssertVector(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < Tolerance,
            $"expected {expected}, got {actual}");

    [Fact]
    public void IdentityParent_ReturnsLocal()
    {
        var local = new Transform(
            new Vector3(1f, 2f, 3f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f),
            Vector3.One);

        var composed = Skeleton.ComposeReference(Transform.Identity, local);

        AssertVector(local.Position, composed.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(local.Rotation, composed.Rotation)) > 1f - Tolerance);
        AssertVector(local.Scale, composed.Scale);
    }

    [Fact]
    public void TranslationChain_Accumulates()
    {
        var parent = new Transform(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One);
        var local = new Transform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One);

        var composed = Skeleton.ComposeReference(parent, local);

        AssertVector(new Vector3(1f, 1f, 0f), composed.Position);
    }

    [Fact]
    public void RotatedParent_CarriesTheChildAround()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var parent = new Transform(new Vector3(0f, 1f, 0f), rotation, Vector3.One);
        var local = new Transform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One);

        var composed = Skeleton.ComposeReference(parent, local);

        AssertVector(
            parent.Position + Vector3.Transform(local.Position, rotation),
            composed.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, composed.Rotation)) > 1f - Tolerance);
    }

    [Fact]
    public void ScaledParent_ScalesTheChildOffset()
    {
        var parent = new Transform(Vector3.Zero, Quaternion.Identity, new Vector3(2f, 2f, 2f));
        var local = new Transform(new Vector3(1f, 0f, 0f), Quaternion.Identity, Vector3.One);

        var composed = Skeleton.ComposeReference(parent, local);

        AssertVector(new Vector3(2f, 0f, 0f), composed.Position);
        AssertVector(new Vector3(2f, 2f, 2f), composed.Scale);
    }

    [Fact]
    public void ChainOfThree_MatchesTheSingleMatrixProduct()
    {
        var a = new Transform(
            new Vector3(0f, 1f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f),
            Vector3.One);
        var b = new Transform(
            new Vector3(0.2f, 0.3f, -0.1f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f),
            Vector3.One);
        var c = new Transform(
            new Vector3(-0.5f, 0.1f, 0.25f),
            Quaternion.CreateFromAxisAngle(
                Vector3.Normalize(new Vector3(0.5f, 0.5f, 0.7f)), 1.1f),
            Vector3.One);

        var stepped = Skeleton.ComposeReference(Skeleton.ComposeReference(a, b), c);
        var direct = Transform.FromMatrix(c.ToMatrix() * b.ToMatrix() * a.ToMatrix());

        AssertVector(direct.Position, stepped.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(direct.Rotation, stepped.Rotation)) > 1f - Tolerance);
        AssertVector(direct.Scale, stepped.Scale);
    }
}
