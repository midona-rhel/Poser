using System.Numerics;
using System.Runtime.InteropServices;

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
    [FieldOffset(0x60)] public ulong JobHandle;

    [FieldOffset(0x70)] public float Speed;

    [FieldOffset(0x90)] public Vector3 Intensity;

    [FieldOffset(0xA0)] public Vector4 Color;

    /// <summary>Bit 0 set while playing or pending stop; with the job
    /// handle it answers "is this effect still alive".</summary>
    [FieldOffset(0xC4)] public uint ActiveFlag;
}
