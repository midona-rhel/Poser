using System.Collections.Immutable;

namespace Poser.Application.Animation;

public sealed record IdleModTarget(int RaceSexId, string Label, ImmutableArray<int> Slots);
public sealed record IdleModChoices(string Name, int SourceRaceSexId, ImmutableArray<IdleModTarget> Targets)
{
    public IdleModOptions Default => new(Name, 1, [SourceRaceSexId]);
    public void Validate(IdleModOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 128)
            throw new ArgumentException("Enter a mod name (up to 128 characters).");
        if (options.Races.IsDefaultOrEmpty || options.Races.Length > 18 || options.Races.Distinct().Count() != options.Races.Length)
            throw new ArgumentException("Select at least one distinct race and gender.");
        foreach (int race in options.Races)
            if (Targets.FirstOrDefault(t => t.RaceSexId == race) is not { } target || !target.Slots.Contains(options.Slot))
                throw new ArgumentException("The selected standing pose slot is not available for every selected race and gender.");
    }
}
public sealed record IdleModOptions(string Name, int Slot, ImmutableArray<int> Races);
