using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>History's view of the entities a load created: a redo's
/// replacements are bound to the originals, and a receipt resolves through
/// that lifecycle alias. Framework thread.</summary>
public interface ISceneHistoryPort
{
    /// <summary>Connect history to a replacement created by replaying the same
    /// saved entity. Ordinary runtime receipts stay exact and expired.</summary>
    void BindHistoryReplacement(SceneEntityHandle previous, SceneEntityHandle replacement);

    /// <summary>The entity a receipt stands for in history, following a bound
    /// replacement; null when it no longer resolves.</summary>
    SelectionId? ResolveHistoryEntity(SceneEntityHandle handle);
}
