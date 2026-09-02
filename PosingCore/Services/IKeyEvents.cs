using Dalamud.Game.ClientState.Keys;

namespace PosingCore.Services;

public enum KeyEventKind
{
    Down,
    Held,
    Released,
}

/// <summary>Answers true to take the key: the game never sees it.</summary>
public delegate bool KeyEventHandler(VirtualKey key, KeyEventKind kind);

/// <summary>
/// The game's key messages before the game acts on them. A handler that
/// answers true takes the key from the game for that message, which is
/// how a chord Poser binds stays Poser's: the game's own keybind for the
/// same key never runs. Nothing is raised while the game's text input is
/// active.
/// </summary>
public interface IKeyEvents
{
    /// <summary>False when the hook could not be installed; the caller
    /// then polls the key state as before and the game sees the key too.</summary>
    bool Available { get; }

    event KeyEventHandler? KeyEvent;
}
