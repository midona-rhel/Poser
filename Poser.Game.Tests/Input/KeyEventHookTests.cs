using Poser.Application.Input;
using Poser.Game.Input;

namespace Poser.Game.Tests.Input;

public sealed class KeyEventHookTests
{
    [Theory]
    [InlineData(0x100u, 0L, KeyEventKind.Down)]
    [InlineData(0x100u, 1L << 30, KeyEventKind.Held)]
    [InlineData(0x101u, 3L << 30, KeyEventKind.Released)]
    [InlineData(0x104u, 1L << 29, KeyEventKind.Down)]
    [InlineData(0x104u, 3L << 29, KeyEventKind.Held)]
    [InlineData(0x105u, 7L << 29, KeyEventKind.Released)]
    [InlineData(0x104u, 0L, KeyEventKind.Down)] // F10 need not carry Alt context.
    [InlineData(0x105u, 3L << 30, KeyEventKind.Released)] // Alt released before key.
    public void Ordinary_and_system_keys_preserve_press_repeat_and_release(uint message, long flags, KeyEventKind expected)
    {
        Assert.Equal(expected, KeyEventHook.DecodeKeyMessage(message, (nint)flags));
    }

    [Theory]
    [InlineData(0x102u)] // WM_CHAR
    [InlineData(0x103u)] // WM_DEADCHAR
    [InlineData(0x106u)] // WM_SYSCHAR
    [InlineData(0x107u)] // WM_SYSDEADCHAR
    [InlineData(0x200u)] // WM_MOUSEMOVE
    [InlineData(0x112u)] // WM_SYSCOMMAND
    public void Non_key_messages_are_not_shortcut_events(uint message)
    {
        Assert.Null(KeyEventHook.DecodeKeyMessage(message, (nint)0));
    }
}
