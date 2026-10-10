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

/// <summary>The spawnable world assets.</summary>
public interface IWorldAssetCatalog
{
    IReadOnlyList<WorldAsset> Models { get; }
    IReadOnlyList<WorldAsset> Furniture { get; }
    IReadOnlyList<WorldAsset> Effects { get; }
}
