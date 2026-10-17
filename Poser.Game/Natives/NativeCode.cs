using System;
using Dalamud.Plugin.Services;

namespace Poser.Game.Natives;

/// <summary>
/// Code sites inside signed functions: a patch or a reference read sits at a fixed
/// offset in a function patchcheck signs, and its bytes are checked before use, so a
/// patch that moves the instruction fails closed instead of writing elsewhere.
/// </summary>
internal static unsafe class NativeCode
{
    /// <summary>Whether the bytes at <paramref name="address"/> match a pattern of hex
    /// bytes and <c>??</c> wildcards.</summary>
    internal static bool Matches(nint address, string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??")
                continue;
            if (*(byte*)(address + i) != Convert.ToByte(tokens[i], 16))
                return false;
        }
        return true;
    }

    /// <summary>A site at <paramref name="offset"/> into the function
    /// <paramref name="functionSignature"/> finds, accepted only when its bytes match
    /// <paramref name="expectedBytes"/>.</summary>
    internal static bool TryFindSite(ISigScanner scanner, string functionSignature, int offset,
        string expectedBytes, out nint address)
    {
        address = 0;
        if (!scanner.TryScanText(functionSignature, out var function) || function == 0)
            return false;
        if (!Matches(function + offset, expectedBytes))
            return false;
        address = function + offset;
        return true;
    }
}
