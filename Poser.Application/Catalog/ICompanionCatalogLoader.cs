using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Catalog;

/// <summary>Builds the minion and mount list from the game data once.</summary>
public interface ICompanionCatalogLoader
{
    bool IsBuilding { get; }
    /// <summary>Why the last build failed, shown in the picker; null otherwise.</summary>
    string? LastError { get; }
    /// <summary>Starts the build the first time; does nothing after that.</summary>
    void EnsureLoaded();
    /// <summary>Builds again after a failed build; called when the picker opens.</summary>
    void Retry();
}
