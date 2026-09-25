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

/// <summary>Hiding a human body under its clothing.</summary>
public interface IInvisibleSkinService
{
    bool IsHuman(ActorId actor);
    void Request(ActorId actor, Action<string>? onFailure);
}
