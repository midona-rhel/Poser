using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>Shows, hides, locks and removes selected things for every window,
/// and reports what was refused, so the sidebar, menus and sections read alike.</summary>
public sealed class EntityActions(ISelectedEntityActions commands, EntityLockCommands locks, UserNotices notices)
{
    public void ToggleLock(SelectionId id) => notices.Report(locks.Toggle(id), "Lock", refusal: true);
    public void ToggleGroupLock(Guid id) => notices.Report(locks.ToggleGroup(id), "Lock", refusal: true);

    public Task<int?> Remove(SelectionId id) => Remove([id]);

    /// <summary>Shows or hides the ids as one command; every refusal is
    /// reported here so sidebar, menu and section toggles read alike.</summary>
    public int SetVisibility(IReadOnlyList<SelectionId> ids, bool visible)
    {
        var result = commands.SetVisibility(ids, visible);
        notices.Visibility(result);
        return result.AppliedCount;
    }

    public async Task<int?> Remove(IReadOnlyList<SelectionId> ids)
    {
        try
        {
            var result = await commands.Remove(ids);
            notices.Removal(result);
            return result.AppliedCount;
        }
        catch (Exception exception)
        {
            notices.Report(Poser.Domain.Outcome.Fail(exception.Message), "Remove");
            return null;
        }
    }
}
