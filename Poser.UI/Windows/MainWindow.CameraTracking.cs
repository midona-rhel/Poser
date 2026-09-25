using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Presentation;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The camera tracking picker: tracked actors and bones, and the bone choice list.</summary>
public partial class MainWindow
{
    /// <summary>Draws one exact actor and its flat concrete-bone picker.</summary>
    private void DrawCameraTrackingActors(
        Crystarium.FormScope form, CameraId cameraId)
    {
        _cameraTargets.Reconcile(cameraId);
        if (_cameraTargets.Read(cameraId) is not { } camera)
        {
            form.Status("Tracking is unavailable for this camera.");
            return;
        }

        var actor = camera.TrackingActor is { } owner ? _scene.Snapshot.FindActor(owner) : null;
        bool locked = camera.IsLocked;
        form.Switch(
            "Tracking",
            camera.IsTracking,
            value => _cameraTargets.SetTracking(cameraId, value),
            help: "Keep the tracked bones in view every frame",
            disabled: locked);
        form.Dropdown(
            "Mode",
            CameraTrackingModeOptions,
            (int)camera.TrackingMode,
            selected => _cameraTargets.SetTrackingMode(cameraId, (CameraTrackingMode)selected),
            disabled: locked,
            help: "Follow moves the camera with the bones, Pan swings the "
                + "view onto them, Follow and pan blends both");

        form.Actions(
            string.Empty,
            actions =>
            {
                actions.Button(
                    "Select bones",
                    () =>
                    {
                        if (actor != null)
                            OpenCameraBonePicker(cameraId, actor.Id);
                    },
                    style: ControlStyle.Workspace with { Width = UiWidth.Fill },
                    disabled: locked || actor == null,
                    help: actor == null
                        ? "Choose an actor first"
                        : $"Choose exact bones on {ActorNames.Display(_configuration, actor)}",
                    id: "camera-track-select-bones");
                // Picking in the view: a click takes a bone, Ctrl-click
                // keeps adding within the currently followed actor.
                actions.IconButton(
                    TablerIcon.Crosshair,
                    () => global::Poser.UI.Controls.BonePick.Begin(
                        multi: true,
                        bone =>
                        {
                            if (_selection.Primary?.Camera == cameraId)
                                _cameraPane.ToggleTrackedBone(cameraId, bone);
                        },
                        onlyActor: actor?.Id),
                    disabled: locked,
                    help: actor == null
                        ? "Pick bones in the view"
                        : "Pick bones in the view on this actor");
            });

        PumpCameraBonePicker(cameraId, camera, actor);
    }

    private void PumpCameraBonePicker(
        CameraId cameraId,
        CameraTargetReading camera,
        ActorDescriptor? currentActor)
    {
        if (_cameraBonePickerCamera == cameraId &&
            _cameraBonePickerActor is { } actorId &&
            currentActor?.Id == actorId &&
            _scene.Snapshot.FindActor(actorId) is { } actor)
        {
            _cameraBoneChoices = _sidebar.BuildBoneChoices(actor);
            _cameraTrackingBonePicker.UpdateItems(_cameraBoneChoices);
            _cameraTrackingBonePicker.UpdateSelection(
                TrackedBoneKeys(camera, actorId));
        }
        else if (_cameraTrackingBonePicker.IsOpen)
        {
            _cameraTrackingBonePicker.UpdateItems(
                Array.Empty<global::Poser.UI.BoneChoice>());
            _cameraTrackingBonePicker.UpdateSelection(
                new HashSet<string>(StringComparer.Ordinal));
        }
        _cameraTrackingBonePicker.Draw();
    }

    private void OpenCameraBonePicker(
        CameraId cameraId, ActorId actorId)
    {
        if (_cameraTargets.Read(cameraId) is not { IsLocked: false } camera ||
            camera.TrackingActor != actorId ||
            _scene.Snapshot.FindActor(actorId) is not { } actor)
            return;
        _cameraBonePickerCamera = cameraId;
        _cameraBonePickerActor = actorId;
        _cameraBoneChoices = _sidebar.BuildBoneChoices(actor);
        var options = new PickerOptions<global::Poser.UI.BoneChoice>
        {
            Query = CameraBoneSearch,
            Badge = choice => choice.Badge,
        };
        _cameraTrackingBonePicker.OpenMulti(
            $"camera-tracking-bones:{cameraId}:{actorId}",
            ActorNames.Display(_configuration, actor),
            _cameraBoneChoices,
            choice => choice.Label,
            choice => choice.Key,
            TrackedBoneKeys(camera, actorId),
            (choice, _) => ToggleCameraTrackedBone(
                cameraId, actorId, choice),
            options: in options);
    }

    private void ToggleCameraTrackedBone(
        CameraId cameraId,
        ActorId actorId,
        global::Poser.UI.BoneChoice choice)
    {
        var boneId = choice.BoneId;
        if (boneId.Skeleton.Actor != actorId ||
            _selection.Primary is not
                { Kind: SceneEntityKind.Camera, Camera: { } selectedCamera }
            || selectedCamera != cameraId ||
            _cameraTargets.Read(cameraId) is not { IsLocked: false } current ||
            current.TrackingActor != actorId)
            return;
        _cameraPane.ToggleTrackedBone(cameraId, boneId);
    }


    private HashSet<string> TrackedBoneKeys(
        CameraTargetReading camera, ActorId actorId) =>
        camera.TrackedBones
            .Where(id => id.Skeleton.Actor == actorId)
            .Select(id => id.ToString())
            .ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<global::Poser.UI.BoneChoice> CameraBoneSearch(string query) =>
        query.Length == 0
            ? _cameraBoneChoices
            : _cameraBoneChoices.Where(choice => choice.SearchText.Contains(
                query, StringComparison.OrdinalIgnoreCase)).ToArray();
}
