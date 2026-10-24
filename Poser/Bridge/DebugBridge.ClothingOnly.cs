#if DEBUG
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Poser.Application.Appearance;
using Poser.Domain.Identity;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private async Task<string> ClothingOnly(Dictionary<string, string> query)
    {
        var service = (IInvisibleSkinService)_services.GetService(typeof(IInvisibleSkinService))!;
        var id = await _framework.RunOnFrameworkThread(() =>
        {
            var actor = FindActor(query.GetValueOrDefault("actor", "0"));
            return actor == null ? (ActorId?)null : _bindings.GetActorId(actor);
        });
        if (id == null) return Json(new { error = "No such actor." });
        string? failure = null;
        if (query.GetValueOrDefault("apply") == "1")
            failure = await service.Apply(id.Value, CancellationToken.None);
        return await _framework.RunOnFrameworkThread(() =>
            Json(new { applied = service.IsApplied(id.Value), failure }));
    }
}
#endif
