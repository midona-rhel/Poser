namespace Poser.Domain.Actors;

/// <summary>
/// Creates the internal game names for spawned actors. These use two words
/// without digits for compatibility with Penumbra's player-name validation
/// (see #427). The displayed scene name can be different.
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

    /// <summary>Uses spelled-out numbers below 100, then a unique letter code
    /// such as "Poser Xaa". Negative slot numbers are rejected.</summary>
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

        // The X prefix keeps these codes distinct from the number words.
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
