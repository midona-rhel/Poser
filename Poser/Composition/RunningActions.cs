using System;
using Microsoft.Extensions.DependencyInjection;
using Poser.Application.Animation;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Integration;

namespace Poser.Composition;

/// <summary>Everything that counts as "an action is running" for <see cref="ActionGate"/>,
/// in one list. An action added to Poser that takes more than one frame belongs here.</summary>
internal static class RunningActions
{
    public static void Register(IServiceProvider services)
    {
        var gate = services.GetRequiredService<ActionGate>();
        void Add(ActionKind kind, Func<string?> running) =>
            gate.Register(() => running() is { } name ? new RunningAction(name, kind) : null);

        var scenes = services.GetRequiredService<ISceneWorkflow>();
        Add(ActionKind.Long, () => !scenes.Busy ? null
            : scenes.Progress?.Kind == SceneOperationKind.Save ? "Save scene" : "Load scene");

        var characterFiles = services.GetRequiredService<CharacterFileSession>();
        Add(ActionKind.Long, () => !characterFiles.Busy ? null
            : characterFiles.Progress?.Kind == McdfOperationKind.Export ? "Save character file" : "Apply character file");

        var idleExport = services.GetRequiredService<IdleModExport>();
        Add(ActionKind.Long, () => idleExport.Busy ? "Export idle" : null);

        var poseImport = services.GetRequiredService<Game.Posing.PoseImportCoordinator>();
        Add(ActionKind.Quick, () => poseImport.IsImportBusy ? "Import pose" : null);

        var bake = services.GetRequiredService<IIkBake>();
        Add(ActionKind.Quick, () => bake.IsPending ? "Disable IK" : null);

        var expression = services.GetRequiredService<IExpressionPreview>();
        Add(ActionKind.Quick, () => expression.IsBaking ? "Bake expression" : null);

        var undo = services.GetRequiredService<UndoJournal>();
        Add(ActionKind.Quick, () => undo.IsRestoring ? "Undo" : null);

        var gestures = services.GetRequiredService<TransformGestureService>();
        Add(ActionKind.Drag, () => gestures.ActiveGesture != null ? "Drag" : null);
    }
}
