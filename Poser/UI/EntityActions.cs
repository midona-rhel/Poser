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
