using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Game.Services;

/// <summary>Whether overlays can be created at all.</summary>
public interface IOverlayNodeService
{
    bool IsAvailable { get; }
}
