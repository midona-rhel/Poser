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

/// <summary>Baking an IK chain into the pose.</summary>
public interface IIkBake
{
    (TransformTargetId Target, string Text)? Note { get; }
    bool IsPending { get; }
    bool CanBake(TransformTargetId target);
    IReadOnlyList<IBone> AffectedChain(TransformTargetId target);
    GestureResult Begin(TransformTargetId target);
}
