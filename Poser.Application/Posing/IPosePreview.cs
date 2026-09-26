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

/// <summary>The pose preview: a rendered body with its own camera.</summary>
public interface IPosePreview
{
    bool IsActive { get; }
    string? StatusText { get; }
    string? RefusalText { get; }
    nint TextureHandle { get; }
    Vector2 TextureSize { get; }
    void Rotate(float yawDelta, float pitchDelta = 0f);
    void Zoom(float distanceDelta);
    void Pan(float viewDelta);
    void ResetCamera();
    void Close();
}
