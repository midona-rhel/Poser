using System.Numerics;
using Poser.Core;
using Poser.Game.Posing;

namespace Poser.Game.Tests.Posing;

public sealed class PartialPoseFrameTests
{
    [Fact]
    public void Head_scale_does_not_contract_facial_bone_spacing()
    {
        var before = new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var after = before with { Scale = new Vector3(1.05f) };
        var displayed = new Transform(new Vector3(.03f, .08f, .1f),
            Quaternion.Identity, new Vector3(1.05f));
        var applied = new PartialPoseFrame(before, after).ToApply(displayed);
        Assert.Equal(displayed.Position, applied.Position);
        Assert.Equal(Vector3.One, applied.Scale);
    }

    private static readonly Transform AnimatedRoot = new(
        new Vector3(0, 1.5f, 0), Quaternion.Identity, Vector3.One);

    // The moved head from the user's 2026-09-07 report.
    private static readonly Transform PosedRoot = new(
        new Vector3(-.29528597f, 1.0887794f, .52169573f),
        Quaternion.Normalize(new Quaternion(.20728698f, .12671688f, .9455419f, .21662301f)),
        Vector3.One);

    [Fact]
    public void Bake_preserves_face_below_reported_moved_head()
    {
        var frame = new PartialPoseFrame(AnimatedRoot, PosedRoot);
        var face = new Transform(new Vector3(.07f, 1.54f, .11f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f), new Vector3(1, .8f, 1.1f));
        var visible = Reparent(face, frame);
        for (int bake = 0; bake < 10; bake++)
        {
            var desired = frame.ToApply(visible);
            var stack = new BonePoseInfo("mouth", 1);
            stack.Apply(desired, face);
            var delta = Assert.Single(stack.Stacks).Transform;
            var applied = new Transform(face.Position + delta.Position,
                Quaternion.Normalize(face.Rotation * delta.Rotation), face.Scale + delta.Scale);
            visible = Reparent(applied, frame);
            Near(Reparent(face, frame), visible);
        }
    }

    [Fact]
    public void Fabrik_endpoint_world_projection_roundtrips_a_moved_scaled_partial()
    {
        // The animated root is not assumed to be identity.
        var before = AnimatedRoot;
        before.Rotation = Quaternion.CreateFromYawPitchRoll(.4f, -.2f, .7f);
        before.Scale = new Vector3(.8f, 1.3f, 1.1f);
        var after = PosedRoot;
        after.Scale = new Vector3(.8f, 1.3f, 1.1f);
        var frame = new PartialPoseFrame(before, after);
        var raw = new Transform(new Vector3(.15f, 1.62f, -.07f),
            Quaternion.CreateFromYawPitchRoll(.2f, .3f, -.4f), Vector3.One);
        var visible = frame.ToDisplay(raw);
        Near(Reparent(raw, frame), visible);
        Near(raw, frame.ToApply(visible));
        // A duplicate keeps the partial-root scale exactly once.
        Assert.InRange(frame.ToApply(visible).Scale.X, .99999f, 1.00001f);
        visible.Position += new Vector3(.1f, .2f, -.3f);
        Near(visible, frame.ToDisplay(frame.ToApply(visible)));
    }

    private static Transform Reparent(Transform raw, PartialPoseFrame frame)
    {
        var local = Vector3.Transform(raw.Position - frame.Before.Position,
            Quaternion.Inverse(frame.Before.Rotation));
        return new Transform(frame.After.Position + Vector3.Transform(local, frame.After.Rotation),
            Quaternion.Normalize(frame.After.Rotation * Quaternion.Inverse(frame.Before.Rotation) * raw.Rotation),
            raw.Scale / frame.Before.Scale * frame.After.Scale);
    }

    private static void Near(Transform expected, Transform actual)
    {
        Assert.True(Vector3.Distance(expected.Position, actual.Position) < .00001f);
        Assert.True(1 - MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) < .00001f);
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < .00001f);
    }
}
