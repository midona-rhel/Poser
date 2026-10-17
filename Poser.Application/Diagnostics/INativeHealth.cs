using System;
using System.Collections.Generic;

namespace Poser.Application.Diagnostics;

/// <summary>What the check found for one native dependency.</summary>
public enum NativeHealthState
{
    /// <summary>Not found in this game version: the feature using it can't work.</summary>
    Missing,
    /// <summary>Found, but the game's value differs from the one Poser uses.</summary>
    Moved,
    /// <summary>A hook that exists but isn't running now, or wasn't needed yet.</summary>
    Off,
    /// <summary>Found and as Poser expects; for a hook, running.</summary>
    Ok,
    /// <summary>A dependency with no pattern to check it by.</summary>
    Unchecked,
}

/// <summary>One dependency: its group (hooks, functions, patch sites, data, offsets,
/// not checkable), the patchcheck name, the result, the resolved value, why, and the
/// Poser code that uses it.</summary>
public sealed record NativeHealthRow(
    string Group, string Name, NativeHealthState State, string Value, string Detail, string UsedBy);

/// <summary>A check of every native dependency against the running game.</summary>
public sealed record NativeHealthReport(DateTime CheckedAt, bool Complete, IReadOnlyList<NativeHealthRow> Rows);

/// <summary>
/// Every game function, hook, patch site, global and field offset Poser depends on
/// (the generated native registry), checked against the running game.
/// </summary>
public interface INativeHealth
{
    /// <summary>The latest check; hooks are read live.</summary>
    NativeHealthReport Report { get; }

    /// <summary>Runs the check again.</summary>
    void Recheck();
}
