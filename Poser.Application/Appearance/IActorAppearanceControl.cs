using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Application.Presentation;

namespace Poser.Application.Appearance;

/// <summary>Actor appearance actions and readouts; history and ownership stay behind this boundary.</summary>
public interface IActorAppearanceControl
{
    IntegrationAvailability Penumbra { get; }
    IntegrationAvailability Glamourer { get; }
    IntegrationAvailability CustomizePlus { get; }
    GlamourerAccess AppearanceAccess(ActorId actor);
    IntegrationOverrides OverridesFor(ActorId actor);
    IntegrationValue<CollectionAssignment> ReadCollection(ActorId actor);
    IntegrationResult CheckBodyProfileDisplaceable(ActorId actor);
    IntegrationValue<IReadOnlyList<ExternalItem>> ListCollections();
    IntegrationValue<IReadOnlyList<ExternalItem>> ListDesigns();
    IntegrationValue<IReadOnlyList<ExternalItem>> ListBodyProfiles();
    IntegrationResult SetCollection(ActorId actor, Guid collection, string name);
    IntegrationResult ResetCollection(ActorId actor);
    IntegrationResult ApplyDesign(ActorId actor, Guid design, string name);
    IntegrationResult ResetDesign(ActorId actor);
    IntegrationResult SetBodyProfile(ActorId actor, Guid profile, string name);
    IntegrationResult ResetBodyProfile(ActorId actor);
    int? ReadModel(ActorId actor);
    bool OwnsModel(ActorId actor);
    PresentationResult SetModel(ActorId actor, int model);
    PresentationResult ResetModel(ActorId actor);
    IntegrationResult Redraw(ActorId actor);
    IntegrationResult OpenGlamourer(ActorId actor);
    IntegrationValue<Guid> SaveActorDesign(ActorId actor, string name);
}
