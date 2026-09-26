using System;
using System.Runtime.InteropServices;

namespace Poser.Core;

/// <summary>
/// Helper methods for native memory operations.
/// </summary>
public static class NativeHelpers
{
    /// <summary>
    /// Allocates aligned memory for native interop.
    /// </summary>
    /// <param name="sizeInBytes">Size of memory to allocate.</param>
    /// <param name="alignment">Required alignment (e.g., 16 for SIMD).</param>
    /// <returns>Tuple of aligned and unaligned addresses. Use unaligned for freeing.</returns>
    public static (nint Aligned, nint Unaligned) AllocateAlignedMemory(int sizeInBytes, int alignment)
    {
        int alignedSize = sizeInBytes + alignment - 1;
        nint unalignedMemory = Marshal.AllocHGlobal(alignedSize);
        int alignmentOffset = (int)(alignment - (unalignedMemory % alignment));
        nint alignedMemory = unalignedMemory + alignmentOffset;

        return (alignedMemory, unalignedMemory);
    }

    /// <summary>
    /// Frees aligned memory allocated with AllocateAlignedMemory.
    /// </summary>
    public static void FreeAlignedMemory((nint Aligned, nint Unaligned) addrs)
    {
        Marshal.FreeHGlobal(addrs.Unaligned);
    }
}
