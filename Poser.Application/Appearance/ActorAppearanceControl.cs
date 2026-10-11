using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Application.Presentation;

namespace Poser.Application.Appearance;

public sealed class ActorAppearanceControl(
    IntegrationSelectors integration,
    ActorModelIdSession model,
    DisruptiveSteps history) : IActorAppearanceControl
{
    public IntegrationAvailability Penumbra => integration.Penumbra;
    public IntegrationAvailability Glamourer => integration.Glamourer;
    public IntegrationAvailability CustomizePlus => integration.CustomizePlus;
    public GlamourerAccess AppearanceAccess(ActorId actor) => integration.AppearanceAccess(actor);
    public IntegrationOverrides OverridesFor(ActorId actor) => integration.OverridesFor(actor);
    public IntegrationValue<CollectionAssignment> ReadCollection(ActorId actor) => integration.ReadCollection(actor);
    public IntegrationResult CheckBodyProfileDisplaceable(ActorId actor) => integration.CheckBodyProfileDisplaceable(actor);
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListCollections() => integration.ListCollections();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListDesigns() => integration.ListDesigns();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListBodyProfiles() => integration.ListBodyProfiles();
    public int? ReadModel(ActorId actor) => model.Read(actor);
    public bool OwnsModel(ActorId actor) => model.IsOwned(actor);
    public IntegrationResult OpenGlamourer(ActorId actor) => integration.OpenGlamourer(actor);
    public IntegrationValue<Guid> SaveActorDesign(ActorId actor, string name) => integration.SaveActorDesign(actor, name);

    public IntegrationResult SetCollection(ActorId actor, Guid collection, string name) =>
        history.Run(actor, "Set collection", () => integration.SetCollection(actor, collection, name));

    public IntegrationResult ResetCollection(ActorId actor) =>
        history.Run(actor, "Reset collection", () => integration.ResetCollection(actor));

    public IntegrationResult ApplyDesign(ActorId actor, Guid design, string name) =>
        history.Run(actor, "Apply design", () => integration.ApplyDesign(actor, design, name));

    public IntegrationResult ResetDesign(ActorId actor) =>
        history.Run(actor, "Reset design", () => integration.ResetDesign(actor));

    public IntegrationResult SetBodyProfile(ActorId actor, Guid profile, string name) =>
        history.Run(actor, "Set body profile", () => integration.SetBodyProfile(actor, profile, name));

    public IntegrationResult ResetBodyProfile(ActorId actor) =>
        history.Run(actor, "Reset body profile", () => integration.ResetBodyProfile(actor));

    public IntegrationResult Redraw(ActorId actor) =>
        history.Run(actor, "Redraw", () => integration.Redraw(actor));

    public Outcome SetModel(ActorId actor, int value) =>
        ModelStep(actor, "Set model id", () => model.Apply(actor, value));

    public Outcome ResetModel(ActorId actor) =>
        ModelStep(actor, "Reset model id", () => model.Reset(actor));

    private Outcome ModelStep(ActorId actor, string description, Func<Outcome> verb) =>
        history.Run(actor, description, () => IntegrationResult.From(verb())).Outcome;
}
