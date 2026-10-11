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

/// <summary>Loads the model catalog; reports its build.</summary>
public interface IModelCatalogLoader
{
    bool IsBuilding { get; }
    string? LastError { get; }
    void EnsureLoaded();
    void Retry();
}
