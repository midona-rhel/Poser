#if DEBUG
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private const uint KeyDownMessage = 0x0100;
    private const uint KeyUpMessage = 0x0101;

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private VirtualKey[] _messageKeys = [];
    private int _messageFrames;
    private int _messageFrame;

    /// <summary>
    /// Sends the game window the same key messages a keyboard produces: every
    /// key goes down in the order given, the last one repeats the way Windows
    /// repeats a held key, and after <paramref name="frames"/> frames they are
    /// released. Unlike keys written into the game's input frame, these also
    /// reach Dalamud and every plugin.
    /// </summary>
    private void StartKeyMessages(VirtualKey[] keys, int frames)
    {
        if (_messageFrames > 0 || keys.Length == 0)
            return;
        _messageKeys = keys;
        _messageFrames = frames;
        _messageFrame = 0;
        _framework.Update += AdvanceKeyMessages;
    }

    private void AdvanceKeyMessages(IFramework framework)
    {
        nint window = Process.GetCurrentProcess().MainWindowHandle;
        if (_messageFrame == 0)
        {
            foreach (var key in _messageKeys)
                PostKey(window, key, down: true, repeat: false);
        }
        else if (_messageFrame >= _messageFrames)
        {
            foreach (var key in _messageKeys.Reverse())
                PostKey(window, key, down: false, repeat: false);
            _messageFrames = 0;
            _framework.Update -= AdvanceKeyMessages;
            return;
        }
        else if (_messageFrame > 15 && _messageFrame % 2 == 0)
        {
            // Windows repeats only the key pressed last, after a short delay.
            PostKey(window, _messageKeys[^1], down: true, repeat: true);
        }
        _messageFrame++;
    }

    private static void PostKey(nint window, VirtualKey key, bool down, bool repeat)
    {
        long details = 1 | ((long)MapVirtualKeyW((uint)key, 0) << 16);
        if (repeat || !down) details |= 1L << 30;
        if (!down) details |= 1L << 31;
        PostMessageW(window, down ? KeyDownMessage : KeyUpMessage, (nuint)(uint)key, (nint)details);
    }
}
#endif
