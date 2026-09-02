using System;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using PosingCore.Services;

namespace Poser.Game.Input;

/// <summary>
/// Ktisis's input hook, whole: the game's window-message handler for key
/// down and key up, detoured so a handler can take the message before the
/// game's keybinds read it. Returning zero from the handler is what keeps
/// the key from the game; clearing a key state later in the frame does
/// not, because the keybind dispatch has already run.
/// </summary>
public sealed unsafe class KeyEventHook : IKeyEvents, IDisposable
{
    private const string InputNotificationSignature =
        "48 89 5C 24 ?? 55 56 57 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 40 4D 8B F9";
    private const uint WmKeyDown = 0x100;
    private const uint WmKeyUp = 0x101;

    private delegate nint InputNotificationDelegate(nint hWnd, uint message, nint wParam, uint lParam);

    private readonly Hook<InputNotificationDelegate>? _hook;
    private readonly IPluginLog _log;

    public event KeyEventHandler? KeyEvent;

    public bool Available => _hook != null;

    public KeyEventHook(ISigScanner sigScanner, IGameInteropProvider hooks, IPluginLog log)
    {
        _log = log;
        try
        {
            _hook = hooks.HookFromAddress<InputNotificationDelegate>(
                sigScanner.ScanText(InputNotificationSignature), InputNotificationDetour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning($"KeyEventHook: input notification unavailable, chords are polled: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
    }

    private nint InputNotificationDetour(nint hWnd, uint message, nint wParam, uint lParam)
    {
        try
        {
            if ((message == WmKeyDown || message == WmKeyUp)
                && KeyEvent is { } handlers
                && !TextInputActive())
            {
                var key = (VirtualKey)(int)wParam;
                var kind = message == WmKeyUp
                    ? KeyEventKind.Released
                    : (lParam >> 30) != 0 ? KeyEventKind.Held : KeyEventKind.Down;
                bool handled = false;
                foreach (KeyEventHandler handler in handlers.GetInvocationList())
                    handled |= handler(key, kind);
                if (handled)
                    return 0;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"KeyEventHook: handler failed: {ex}");
        }
        return _hook!.Original(hWnd, message, wParam, lParam);
    }

    /// <summary>The game's own text input (chat, a name field) owns every
    /// key while it is active; nothing is raised then.</summary>
    private static bool TextInputActive()
    {
        var module = UIModule.Instance();
        if (module == null)
            return false;
        var atk = module->GetRaptureAtkModule();
        return atk != null && atk->AtkModule.IsTextInputActive();
    }
}
