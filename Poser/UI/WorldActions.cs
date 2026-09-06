using System;
using System.Threading.Tasks;
using Poser.Application.World;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>Reports release results for the inspector and context menus; no native knowledge.</summary>
public sealed class WorldActions(IWorldService world, UserNotices notices)
{
    public async Task Release(SelectionId entity)
    {
        try
        {
            var result = await world.Release(entity);
            if (!result.Success) notices.Refused(result.Detail ?? "That world asset could not be released.");
        }
        catch (Exception ex) { notices.Failed("Release", ex.Message); }
    }

    public async Task ReleaseSceneObjects()
    {
        try
        {
            var result = await world.ReleaseSceneObjects();
            if (!result.Success) notices.Refused(result.Detail ?? "The scene objects could not be released.");
        }
        catch (Exception ex) { notices.Failed("Release", ex.Message); }
    }
}
