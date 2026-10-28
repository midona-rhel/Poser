using System.Runtime.InteropServices;
using Poser.Game.Core;

namespace Poser.Game.Tests.Core;

public sealed class NativeHelpersTests
{
    [Fact]
    public void Every_base_address_remainder_keeps_the_requested_range_inside_the_allocation()
    {
        const int size = 128;
        const int alignment = 16;
        for (int remainder = 0; remainder < alignment; remainder++)
        {
            nint original = 0x1000 + remainder;
            var aligned = NativeHelpers.AlignAddress(original, alignment);

            Assert.Equal((nint)0, aligned % alignment);
            Assert.InRange(aligned - original, (nint)0, (nint)(alignment - 1));
            Assert.True(aligned + size <= original + size + alignment - 1);
            if (remainder == 0)
                Assert.Equal(original, aligned);
        }
    }

    [Fact]
    public void Allocated_range_can_be_written_and_read_before_freeing_the_original_pointer()
    {
        const int size = 128;
        var allocation = NativeHelpers.AllocateAlignedMemory(size, 16);
        try
        {
            Assert.Equal((nint)0, allocation.Aligned % 16);
            Assert.InRange(allocation.Aligned - allocation.Unaligned, (nint)0, (nint)15);
            var expected = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
            Marshal.Copy(expected, 0, allocation.Aligned, size);
            var actual = new byte[size];
            Marshal.Copy(allocation.Aligned, actual, 0, size);
            Assert.Equal(expected, actual);
        }
        finally
        {
            NativeHelpers.FreeAlignedMemory(allocation);
        }
    }
}
