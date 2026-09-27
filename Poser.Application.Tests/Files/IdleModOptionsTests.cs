using Poser.Application.Animation;

namespace Poser.Tests.Files;

public sealed class IdleModOptionsTests
{
    private static IdleModChoices Choices => new("My pose", 801,
        [new(801, "Source", [1, 2, 3, 4, 5, 6]), new(301, "Other", [1, 2, 3, 4])]);

    [Fact]
    public void Default_selects_only_the_captured_actor_race_and_gender()
    {
        Assert.Equal(new[] { 801 }, Choices.Default.Races);
        Assert.Equal(1, Choices.Default.Slot);
        Choices.Validate(Choices.Default);
    }

    [Fact]
    public void Multiple_targets_require_a_shared_slot()
    {
        Choices.Validate(new("Test", 4, [301, 801]));
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("Test", 5, [301, 801])));
    }

    [Fact]
    public void Invalid_targets_name_and_slot_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("Test", 1, [])));
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("Test", 1, [801, 801])));
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("Test", 1, [9999])));
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("", 1, [801])));
        Assert.Throws<ArgumentException>(() => Choices.Validate(new("Test", 0, [801])));
    }
}
