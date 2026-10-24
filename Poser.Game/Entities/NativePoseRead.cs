using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace Poser.Game.Entities;

internal static unsafe class NativePoseRead
{
    internal static hkQsTransformf* Model(hkaPose* pose, int index)
    {
        if (pose == null || index < 0 || index >= pose->ModelPose.Length)
            return null;
        // AccessBoneModelSpace is a WRITE accessor: even DontPropagate changes
        // local/model dirty state. Observers must not invalidate child locals
        // or introduce extra inverse/recomposition passes for anisotropic scale.
        if (pose->ModelInSync == 0)
            pose->SyncModelSpace();
        return pose->ModelPose.Data + index;
    }
}
