using System;
using System.Collections.Generic;

namespace Poser.Game.Validation;

/// <summary>
/// Focused executable gates for the clean posing rewrite. Product feature and
/// visual/UI inventories deliberately do not belong to this catalog.
/// </summary>
public static class LiveScenarioCatalog
{
    public const string BasicSelector = "basic";

    /// <summary>Fast one-cycle confidence gate used by bare `/poser test`.</summary>
    public static IReadOnlyList<string> Basic { get; } = Array.AsReadOnly(new[]
    {
        "selection.actor-bone-clear",
        "transform.actor-components",
        "transform.actor-undo-redo",
        "posing.bone-components",
        "posing.animation-interference",
        "posing.reset-region",
        "posing.copy-paste-pose",
    });

    /// <summary>
    /// Complete clean-core rewrite gate. `full` means exactly these scenarios,
    /// not every feature exposed by the Poser plugin.
    /// </summary>
    public static IReadOnlyList<string> Executable { get; } = Array.AsReadOnly(new[]
    {
        "selection.actor-bone-clear",
        "transform.actor-components",
        "transform.actor-undo-redo",
        "posing.bone-components",
        "posing.animation-interference",
        "posing.reset-region",
        "posing.copy-paste-pose",
    });
}
