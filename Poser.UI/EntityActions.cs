using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>Shared UI reporting for entity commands; ownership and history stay behind the port.</summary>
public sealed class EntityActions(SelectionEntityCommands commands, UserNotices notices)
{
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
            notices.Failed("Remove", exception.Message);
            return null;
        }
    }
}
