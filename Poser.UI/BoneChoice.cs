using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>One bone in a bone picker. The camera's tracking picker and the
/// IK bone target picker both use it.</summary>
public sealed record BoneChoice(
    string Key,
    string Label,
    string SearchText,
    BoneId BoneId,
    string? Badge)
{
    /// <summary>
    /// The bones that match what was typed, best match first: a bone whose name is the
    /// text, then names that start with it, then names with a word that starts with it,
    /// then names that contain it. Bones that only match through their group or through
    /// the game's own bone name come last. Each typed word has to match somewhere.
    /// </summary>
    public static IReadOnlyList<BoneChoice> Search(IReadOnlyList<BoneChoice> choices, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return choices;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return choices
            .Where(choice => words.All(word =>
                choice.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select((choice, order) => (choice, order, rank: Rank(choice.Label, query)))
            .OrderBy(match => match.rank).ThenBy(match => match.order)
            .Select(match => match.choice)
            .ToArray();
    }

    private static int Rank(string name, string query)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (name.Equals(query, ignoreCase)) return 0;
        if (name.StartsWith(query, ignoreCase)) return 1;
        if (name.Split(' ').Any(word => word.StartsWith(query, ignoreCase))) return 2;
        return name.Contains(query, ignoreCase) ? 3 : 4;
    }
}
