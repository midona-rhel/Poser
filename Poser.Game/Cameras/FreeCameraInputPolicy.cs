using System;
using Dalamud.Game.ClientState.Keys;

namespace Poser.Game.Cameras;

/// <summary>
/// THE FREE CAMERA'S KEY MAP, AND WHAT IT IS ALLOWED TO TAKE OFF THE GAME.
///
/// <para>The map itself lives here rather than inline in the input detour
/// because the detour is unsafe pointer code that no test can reach, and
/// because the fly keys are named in two places — the flying camera and the
/// LOCKED camera both consume them. Two hand-written copies is how Q and E
/// outlived the map that dropped them; <see cref="MovementKeys"/> is the one
/// list both sites read.</para>
///
/// <para>Consuming a key is not free: the game's own keybinds read the same
/// input frame, so every key Poser eats is a chord the photographer no longer
/// has. The rule is that a key is taken only on a frame it did something.
/// Being LIVE is not doing something — a free camera sits live for the whole
/// session — so a modifier that moves nothing on its own stays the game's
/// until the camera is actually being flown.</para>
///
/// <para>READING AND CONSUMING ARE TWO DIFFERENT BUFFERS, and that is the
/// standing invariant of the whole input path (Brio's split, verbatim:
/// <c>InputManagerService.IsKeyDown</c> reads Dalamud's <c>IKeyState</c> while
/// <c>GameInputService.HandleInputDetour</c> only writes zeroes). The game may
/// invoke the input detour more than once per rendered frame, so a detour that
/// READ the same <c>KeyboardFrame</c> it zeroes would see every key already up
/// on the later invocation: the axes would resolve to zero (that frame loses
/// its motion) and the speed would fall back off a still-held modifier — which
/// is why the stutter was felt specifically while holding Shift or Ctrl. The
/// reader below is a plain key predicate over an independent buffer, so
/// <see cref="Resolve"/> returns the same answer however many times a frame
/// asks.</para>
/// </summary>
internal static class FreeCameraInputPolicy
{
    /// <summary>
    /// Every key that MOVES the camera, and therefore every key a flying (or
    /// locked) free camera takes off the game. The user's map, 2026-08-15:
    /// W A S D travel, Space rises, C falls.
    /// </summary>
    public static readonly VirtualKey[] MovementKeys =
    [
        VirtualKey.W,
        VirtualKey.A,
        VirtualKey.S,
        VirtualKey.D,
        VirtualKey.SPACE,
        VirtualKey.C,
    ];

    /// <summary>
    /// The speed MODIFIERS — Shift fast, Ctrl slow (user's map, 2026-08-15).
    /// Separate from <see cref="MovementKeys"/> precisely because they move
    /// nothing alone, which is what earns them the narrower consumption gate.
    /// </summary>
    public static readonly VirtualKey[] SpeedModifierKeys =
    [
        VirtualKey.SHIFT,
        VirtualKey.CONTROL,
    ];

    /// <summary>How long a reported ImGui text focus stands before it lapses.
    /// The report can only be renewed by a DRAWN frame, so it must expire on
    /// its own: a hidden HUD stops drawing entirely, and a stamp that never
    /// lapsed would leave the camera permanently deaf. Two or three frames is
    /// long enough to bridge a draw the game skipped.</summary>
    public const long UiTextFocusLapseMs = 250;

    /// <summary>Forward/back from W and S. Negative is forward: the input
    /// vector's Z is the camera's look ray, which points away from the
    /// viewer.</summary>
    public static int ForwardBackAxis(bool w, bool s) =>
        (w ? -1 : 0) + (s ? 1 : 0);

    /// <summary>Strafe from A and D.</summary>
    public static int LeftRightAxis(bool a, bool d) =>
        (a ? -1 : 0) + (d ? 1 : 0);

    /// <summary>Rise/fall from Space and C. The axis travels with the input
    /// vector, so it is the camera's up rather than the world's — pitched
    /// down, rising also carries you forward, exactly as Brio flies. Move2D
    /// is the switch that pins it to world vertical.</summary>
    public static int UpDownAxis(bool space, bool c) =>
        (space ? 1 : 0) + (c ? -1 : 0);

    /// <summary>
    /// Whether the free camera is being FLOWN this frame: any of the three
    /// movement axes is being driven. The axes are already the resolved sum of
    /// every key that feeds them, so this asks the question once for W/A/S/D
    /// and the Space/C rise-fall pair together.
    /// </summary>
    public static bool IsFlying(int forwardBack, int leftRight, int upDown) =>
        forwardBack != 0 || leftRight != 0 || upDown != 0;

    /// <summary>
    /// What the fly speed is multiplied by this frame. Shift beats Ctrl when
    /// both are held — a fast modifier and a slow one cannot both apply, and
    /// the one the user pressed for is the one that wins the chord in every
    /// tool that has both.
    /// </summary>
    public static float SpeedMultiplier(
        bool shift, bool ctrl, float fastMultiplier, float slowMultiplier)
    {
        if (shift)
            return fastMultiplier;
        if (ctrl)
            return slowMultiplier;
        return 1f;
    }

    /// <summary>
    /// Whether the UI's text-focus report still stands. <paramref
    /// name="sinceMs"/> is the age of the last report; a negative age is a
    /// clock that went backwards and reads as lapsed rather than as eternal.
    /// </summary>
    public static bool UiTextFocusHolds(bool reported, long sinceMs) =>
        reported && sinceMs >= 0 && sinceMs < UiTextFocusLapseMs;

    /// <summary>
    /// The keys the whole-frame consumption (<c>ConsumeAllGameInput</c>) never
    /// takes, whatever the user asked for. Escape and Return are how a player
    /// leaves a game dialog, and a plugin that swallows them strands them.
    /// </summary>
    public static bool NeverConsumed(int virtualKey) =>
        virtualKey == (int)VirtualKey.ESCAPE
        || virtualKey == (int)VirtualKey.RETURN;

    /// <summary>
    /// The whole frame of free-camera input, resolved from one key reader in
    /// one place. Pure: the same reader answers the same way however many
    /// times the game invokes the input detour for a rendered frame, which is
    /// what makes the speed multiplier stable under a held modifier.
    /// <paramref name="keyDown"/> is null only where no key source exists (the
    /// test constructor), and reads as every key up rather than throwing.
    /// </summary>
    public static FreeCameraFrameInput Resolve(
        Func<VirtualKey, bool>? keyDown,
        float fastMultiplier,
        float slowMultiplier)
    {
        if (keyDown == null)
            return new FreeCameraFrameInput(0, 0, 0, 1f);

        return new FreeCameraFrameInput(
            ForwardBackAxis(keyDown(VirtualKey.W), keyDown(VirtualKey.S)),
            LeftRightAxis(keyDown(VirtualKey.A), keyDown(VirtualKey.D)),
            UpDownAxis(keyDown(VirtualKey.SPACE), keyDown(VirtualKey.C)),
            SpeedMultiplier(
                keyDown(VirtualKey.SHIFT),
                keyDown(VirtualKey.CONTROL),
                fastMultiplier,
                slowMultiplier));
    }
}

/// <summary>One rendered frame's free-camera input: the three resolved axes
/// and the speed multiplier the held modifiers ask for. A value, so a second
/// resolve of the same key state compares equal to the first.</summary>
internal readonly record struct FreeCameraFrameInput(
    int ForwardBack,
    int LeftRight,
    int UpDown,
    float SpeedMultiplier)
{
    /// <summary>Whether the camera is being FLOWN this frame — the gate the
    /// speed modifiers' consumption sits behind.</summary>
    public bool IsFlying =>
        FreeCameraInputPolicy.IsFlying(ForwardBack, LeftRight, UpDown);
}
