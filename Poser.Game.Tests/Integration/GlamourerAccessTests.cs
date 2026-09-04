using Poser.Domain.Integration;
using Poser.Game.Integration;
using System.Reflection;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Game.Journal;

namespace Poser.Game.Tests;

public sealed class GlamourerAccessTests
{
    [Fact]
    public void Journal_read_races_keep_typed_refusal_without_recording_or_mutating()
    {
        var port = DispatchProxy.Create<IIntegrationRuntimePort, ReadRaceProxy>();
        var session = new ActorIntegrationSession(port, null!, new SessionSource());
        // A refusal must return before touching journal/bindings or invoking
        // any mutation; the proxy throws for every unexpected native call.
        var wardrobe = new WardrobeSession(null!, session, null!);
        var customize = new CustomizeSession(null!, session, null!);
        var actor = ActorId.New();
        IntegrationResult[] results =
        [
            wardrobe.SetItem(actor, EquipSlot.Head, 1, 0, 0, "item"),
            wardrobe.SetDye(actor, EquipSlot.Head, 0, 1, "dye"),
            wardrobe.SetFacewear(actor, 1, "facewear"),
            wardrobe.SetSwitch(actor, MetaSwitch.HatVisible, true),
            wardrobe.SetOutfit(actor, "outfit", _ => new WardrobeSlot(1, 0, 0)),
            customize.Set(actor, CustomizeKey.Height, 50, "height"),
            customize.SetMany(actor, new Dictionary<CustomizeKey, int>(), "look"),
        ];
        Assert.All(results, result => Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal));
    }

    private sealed class SessionSource : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
    }

    public class ReadRaceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IIntegrationRuntimePort.ProbeGlamourerAccess) => GlamourerAccess.Editable,
            nameof(IIntegrationRuntimePort.CaptureGlamourerState) => IntegrationValue<string>.Ok("baseline"),
            nameof(IIntegrationRuntimePort.GetActorName) => IntegrationValue<string>.Ok("Actor"),
            nameof(IIntegrationRuntimePort.GetWardrobeState) => IntegrationValue<WardrobeState>.Refused(GlamourerAccess.ForeignHeld),
            nameof(IIntegrationRuntimePort.GetCustomizeState) => IntegrationValue<CustomizeState>.Refused(GlamourerAccess.ForeignHeld),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    [Theory]
    [InlineData(0, true, null, false, GlamourerAccessKind.Editable)]
    [InlineData(1, true, null, false, GlamourerAccessKind.Editable)]
    [InlineData(0, false, null, false, GlamourerAccessKind.Unavailable)]
    [InlineData(2, false, null, false, GlamourerAccessKind.Unavailable)]
    [InlineData(99, false, null, false, GlamourerAccessKind.Unavailable)]
    [InlineData(6, false, 6, false, GlamourerAccessKind.ForeignHeld)]
    [InlineData(6, false, 0, true, GlamourerAccessKind.PoserHeld)]
    [InlineData(6, false, 1, true, GlamourerAccessKind.PoserHeld)]
    [InlineData(6, false, 0, false, GlamourerAccessKind.Unavailable)]
    [InlineData(6, false, 2, false, GlamourerAccessKind.Unavailable)]
    [InlineData(6, false, 99, false, GlamourerAccessKind.Unavailable)]
    public void Vendor_codes_are_classified_without_inventing_ownership(
        int code, bool state, int? keyedCode, bool keyedState, GlamourerAccessKind expected)
    {
        var result = IntegrationRuntimePort.ClassifyAccess(code, state, keyedCode, keyedState);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(expected == GlamourerAccessKind.Editable, result.CanEdit);
    }
}
