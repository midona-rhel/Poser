namespace Poser.Domain.Actors;

/// <summary>
/// In-game object names for Poser-spawned bodies. The spawn's self-directed
/// appearance copy makes Penumbra identify the clone by its OWN name, and
/// Penumbra only accepts a player name of two parts (one space), each 2-15
/// characters, an uppercase A-Z first letter and lowercase a-z, apostrophe or
/// hyphen after it. A digit fails (InvalidIdentifier, 16) and Glamourer then
/// cannot read the actor either (#427), so every slot gets a letters-only name.
/// </summary>
public static class SpawnNames
{
    public const string FirstName = "Poser";

    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety",
    };

    /// <summary>"Poser One".."Poser Nineteen" for 1-19 (unchanged), "Poser Zero",
    /// "Poser Twenty".."Poser Ninety-nine", then "Poser X" + base-26 letters
    /// (at least two) for 100 and up: "Poser Xaa", "Poser Xab", ...</summary>
    public static string ForSlot(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return $"{FirstName} {SecondName(index)}";
    }

    private static string SecondName(int index)
    {
        if (index < Ones.Length)
            return Ones[index];
        if (index < 100)
        {
            var tens = Tens[index / 10];
            var ones = index % 10;
            return ones == 0 ? tens : $"{tens}-{Ones[ones].ToLowerInvariant()}";
        }

        // No English number word starts with X, so this series cannot meet
        // the spelled-out one. int.MaxValue needs 7 letters: 8 with the X.
        var value = index - 100;
        var letters = new Stack<char>();
        do
        {
            letters.Push((char)('a' + value % 26));
            value /= 26;
        }
        while (value > 0);
        if (letters.Count < 2)
            letters.Push('a');
        return "X" + new string(letters.ToArray());
    }
}
