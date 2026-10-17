using System.Numerics;
using System.Runtime.InteropServices;
using Poser.Game.Natives;

namespace Poser.Game.WorldObjects;

/// <summary>
/// The playable half of a world VFX, reached through
/// <c>VfxObject.VfxResourceInstance</c>. ClientStructs does not model it,
/// so the fields are stated here at Brio's verified offsets
/// (<c>Brio/Game/VFX/Intertop/VFXData.cs</c>) — only what Poser reads or
/// writes: the playback speed and the liveness flag.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xD0)]
public unsafe struct VfxResourceInstance
{
    [FieldOffset(NativeFields.VfxResourceInstance_JobHandle)] public ulong JobHandle;

    [FieldOffset(NativeFields.VfxResourceInstance_Speed)] public float Speed;

    // The per-axis scale the world matrix rows are multiplied by (patchcheck
    // VfxResourceInstance.Scale; native setter 0x14038FCF0).
    [FieldOffset(NativeFields.VfxResourceInstance_Scale)] public Vector3 Scale;

    [FieldOffset(NativeFields.VfxResourceInstance_Color)] public Vector4 Color;

    /// <summary>Bit 0 set while playing or pending stop; with the job
    /// handle it answers "is this effect still alive".</summary>
    [FieldOffset(NativeFields.VfxResourceInstance_ActiveFlag)] public uint ActiveFlag;
}
