using Dalamud.Game.ClientState.Keys;

namespace Poser.Game.Cameras;

/// <summary>
/// WHAT A LIVE FREE CAMERA IS ALLOWED TO TAKE OFF THE GAME.
///
/// <para>Consuming a key is not free: the game's own keybinds read the same
/// input frame, so every key Poser eats is a chord the photographer no longer
/// has. The rule is that a key is taken only on a frame it did something.
/// Being LIVE is not doing something — a free camera sits live for the whole
/// session — so a modifier that moves nothing on its own stays the game's
/// until the camera is actually being flown.</para>
/// </summary>
internal static class FreeCameraInputPolicy
{
    /// <summary>
    /// Whether the free camera is being FLOWN this frame: any of the three
    /// movement axes is being driven. The axes are already the resolved sum of
    /// every key that feeds them, so this asks the question once for W/A/S/D,
    /// Q/E and the Space/Shift rise-fall pair together.
    /// </summary>
    public static bool IsFlying(int forwardBack, int leftRight, int upDown) =>
        forwardBack != 0 || leftRight != 0 || upDown != 0;

    /// <summary>
    /// The keys the whole-frame consumption (<c>ConsumeAllGameInput</c>) never
    /// takes, whatever the user asked for. Escape and Return are how a player
    /// leaves a game dialog, and a plugin that swallows them strands them.
    /// </summary>
    public static bool NeverConsumed(int virtualKey) =>
        virtualKey == (int)VirtualKey.ESCAPE
        || virtualKey == (int)VirtualKey.RETURN;
}
