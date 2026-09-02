using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Scene;

namespace Poser.Services;

/// <summary>The spawnable world assets.</summary>
public interface IWorldAssetCatalog
{
    IReadOnlyList<WorldAsset> Models { get; }
    IReadOnlyList<WorldAsset> Effects { get; }
}
