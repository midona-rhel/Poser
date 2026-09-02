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

/// <summary>The whole-scene save and load transaction as a surface drives it.</summary>
public interface ISceneWorkflow
{
    long EstimatedAppearanceBytes { get; }
    SceneProgress? Progress { get; }
    OperationReceipt? Receipt { get; }
    bool Busy { get; }
    void Cancel();
    SceneActionResult BeginSave( string path, string? description = null, SceneSaveOptions? options = null);
    SceneActionResult BeginLoad( string path, SceneLoadOptions? options = null);
    ScenePendingStructure? PendingSceneStructure { get; }
    void ClearPendingStructure();
}
