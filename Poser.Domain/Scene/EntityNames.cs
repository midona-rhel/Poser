using System.Globalization;
using System.Numerics;

namespace Poser.Domain.Scene;

/// <summary>Numbered display names within one entity family's live name series.</summary>
public static class EntityNames
{
    public static string Next(string seed, IEnumerable<string> existingNames)
    {
        var (stem, highest) = Split(seed.Trim());
        foreach (var name in existingNames)
        {
            var (otherStem, number) = Split(name.Trim());
            if (string.Equals(stem, otherStem, StringComparison.OrdinalIgnoreCase))
                highest = BigInteger.Max(highest, BigInteger.Max(number, BigInteger.One));
        }
        return $"{stem} {(highest + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    private static (string Stem, BigInteger Number) Split(string name)
    {
        int space = name.LastIndexOf(' ');
        if (space > 0 && BigInteger.TryParse(name.AsSpan(space + 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return (name[..space], number);
        return (name, BigInteger.Zero);
    }
}
