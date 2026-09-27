using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

internal static class IdlePoseLocalTransform
{
    public static PoseTransform FromModel(string boneName, Transform model, Transform? parent)
    {
        var child = Checked(model.Position, model.Rotation, model.Scale, "model");
        if (parent is not { } value) return child;
        var basis = Checked(value.Position, value.Rotation, value.Scale, "parent");

        // Havok Qs composition stores R = Rp * Rl and S = Sp * Sl separately,
        // with T = Tp + Rp(Sp * Tl). Invert those components, not SRT matrices:
        // matrix inversion introduces shear for rotated, non-uniform scales that
        // the game's Qs model pose never contained. Keep negative scale signs too.
        var inverseRotation = Quaternion.Conjugate(basis.Rotation);
        return Checked(
            Vector3.Transform(child.Position - basis.Position, inverseRotation) / basis.Scale,
            inverseRotation * child.Rotation,
            child.Scale / basis.Scale,
            "local");

        PoseTransform Checked(Vector3 position, Quaternion rotation, Vector3 scale, string space)
        {
            if (PoseTransform.TryCreate(position, rotation, scale, out var result, out var error))
                return result;
            throw new InvalidOperationException($"Idle export: bone '{boneName}' has an invalid {space} transform. {error}");
        }
    }
}
