using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Customize+ profiles.</summary>
public interface ICustomizePlusPort
{
    IntegrationAvailability CustomizePlus { get; }

    /// <summary>Saved (normal) profiles only.</summary>
    IntegrationValue<IReadOnlyList<ExternalItem>> GetBodyProfiles();

    /// <summary>Returns the actor's selected saved Customize+ profile.</summary>
    IntegrationValue<Guid?> GetActiveSavedBodyProfile(ActorId actor);

    IntegrationValue<string> GetBodyProfileJson(Guid profile);

    IntegrationValue<Guid> ApplyTemporaryBodyProfile(ActorId actor, string profileJson);

    /// <summary>Deletes the temporary profile using the ID returned when Poser
    /// created it. Deleting by actor could remove another plugin's profile.</summary>
    IntegrationResult DeleteTemporaryBodyProfileById(Guid profile);
}
