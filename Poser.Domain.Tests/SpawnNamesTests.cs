using Poser.Domain.Actors;

namespace Poser.Domain.Tests;

public sealed class SpawnNamesTests
{
    public static TheoryData<int> Slots()
    {
        var data = new TheoryData<int>();
        for (var index = 0; index <= 300; index++)
            data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void Every_slot_name_passes_Penumbra(int index)
    {
        var name = SpawnNames.ForSlot(index);
        Assert.True(IsValidPenumbraPlayerName(name), $"{index} -> \"{name}\"");
    }

    [Fact]
    public void Slot_names_are_unique()
    {
        var names = Enumerable.Range(0, 301).Select(SpawnNames.ForSlot).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0, "Poser Zero")]
    [InlineData(1, "Poser One")]
    [InlineData(19, "Poser Nineteen")]
    [InlineData(20, "Poser Twenty")]
    [InlineData(21, "Poser Twenty-one")]
    [InlineData(99, "Poser Ninety-nine")]
    [InlineData(100, "Poser Xaa")]
    [InlineData(101, "Poser Xab")]
    public void Known_slots_keep_their_names(int index, string expected)
        => Assert.Equal(expected, SpawnNames.ForSlot(index));

    [Fact]
    public void Largest_slot_still_passes_Penumbra()
        => Assert.True(IsValidPenumbraPlayerName(SpawnNames.ForSlot(int.MaxValue)));

    // Port of Penumbra.GameData ActorIdentifierFactory.VerifyPlayerName.
    private static bool IsValidPenumbraPlayerName(string name)
    {
        if (name.Length > 21)
            return false;
        var parts = name.Split(' ');
        if (parts.Length != 2)
            return false;
        foreach (var part in parts)
        {
            if (part.Length is < 2 or > 15)
                return false;
            if (part[0] is < 'A' or > 'Z')
                return false;
            for (var i = 1; i < part.Length; i++)
            {
                var c = part[i];
                if (c is not ((>= 'a' and <= 'z') or '-' or '\''))
                    return false;
            }
        }
        return true;
    }
}
