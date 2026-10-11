using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Customize+ profiles.</summary>
public interface ICustomizePlusPort
{
    IntegrationAvailability CustomizePlus { get; }

    /// <summary>Saved (normal) profiles only.</summary>
    IntegrationValue<IReadOnlyList<ExternalItem>> GetBodyProfiles();

    /// <summary>The profile exposed by the provider's active-ID query.
    /// Customize+ 6.x omits temporary profiles: null does not establish their
    /// absence. Poser-owned profile contents must come from retained ownership.</summary>
    IntegrationValue<BodyProfileProbe> ProbeBodyProfile(ActorId actor);

    IntegrationValue<string> GetBodyProfileJson(Guid profile);

    IntegrationValue<Guid> ApplyTemporaryBodyProfile(ActorId actor, string profileJson);

    /// <summary>Deletes Poser's temporary profile by its OWN id — the one
    /// ownership-safe cleanup primitive. There is deliberately no
    /// delete-by-actor: that would remove whichever temporary profile is
    /// active, including another plugin's.</summary>
    IntegrationResult DeleteTemporaryBodyProfileById(Guid profile);
}
