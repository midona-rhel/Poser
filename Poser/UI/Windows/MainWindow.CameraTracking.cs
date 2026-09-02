using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Transforms;
using Poser.Domain.Companions;
using Poser.Game.Posing;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The camera tracking picker: tracked actors and bones, and the bone choice list.</summary>
public partial class MainWindow
{
    /// <summary>Draws one exact actor and its flat concrete-bone picker.</summary>
    private void DrawCameraTrackingActors(
        Crystarium.FormScope form, IVirtualCamera camera)
    {
        if (_bindings.GetCameraId(camera) is not { } cameraId)
        {
            form.Status("Tracking is unavailable for this camera.");
            return;
        }

        var actor = ReconcileCameraTrackingActor(cameraId, camera);
        bool locked = camera.IsLocked;
        form.Switch(
            "Tracking",
            camera.IsTracking,
            value => camera.IsTracking = value,
            help: "Keep the tracked bones in view every frame",
            disabled: locked);
        form.Dropdown(
            "Mode",
            CameraTrackingModeOptions,
            (int)camera.TrackingMode,
            selected => camera.TrackingMode = (CameraTrackingMode)selected,
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
                            OpenCameraBonePicker(cameraId, actor.Id, camera);
                    },
                    style: ControlStyle.Workspace with { Width = UiWidth.Fill },
                    disabled: locked || actor == null,
                    help: actor == null
                        ? "Choose an actor first"
                        : $"Choose exact bones on {ActorNames.Display(actor)}",
                    id: "camera-track-select-bones");
                // Picking in the view: a click takes a bone, Ctrl-click
                // keeps adding. Another actor's bone moves the tracking
                // to that actor, as the list does.
                actions.IconButton(
                    TablerIcon.Crosshair,
                    () => global::Poser.UI.Controls.BonePick.Begin(
                        multi: true,
                        bone =>
                        {
                            if (ResolveExactCamera(cameraId, camera) && !camera.IsLocked)
                                _cameraPane.ToggleTrackedBone(camera, bone);
                        },
                        onlyActor: actor?.Id),
                    disabled: locked,
                    help: actor == null
                        ? "Pick bones in the view"
                        : "Pick bones in the view on this actor");
            });

        PumpCameraBonePicker(cameraId, camera, actor);
    }

    /// <summary>Prunes stale and mixed tracking state, then resolves the one
    /// exact actor that currently owns tracking.</summary>
    private ActorDescriptor? ReconcileCameraTrackingActor(
        CameraId cameraId, IVirtualCamera camera)
    {
        if (!ResolveExactCamera(cameraId, camera))
            return null;
        if (camera.IsTargetLocked && camera.TargetActorId is null)
            _cameraService.ClearTargetActor(camera);

        ActorId? trackedOwner = null;
        for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
        {
            var tracked = camera.TrackedBones[i];
            if (_bindings.GetBoneId(tracked) is not { } boneId ||
                _bindings.Resolve(boneId) is not
                    { Success: true, Value: { } current } ||
                !ReferenceEquals(current, tracked))
            {
                camera.TrackedBones.RemoveAt(i);
                continue;
            }
            trackedOwner ??= boneId.Skeleton.Actor;
            if (trackedOwner != boneId.Skeleton.Actor)
                camera.TrackedBones.RemoveAt(i);
        }

        if (camera.TargetActorId is { } targetId)
        {
            if (!TryResolveExactActor(targetId, out var targetActor) ||
                !ReferenceEquals(camera.TargetActor, targetActor) ||
                ResolveActorDescriptor(targetId) is not { } targetDescriptor)
            {
                _cameraService.ClearTargetActor(camera);
            }
            else
            {
                if (trackedOwner is { } owner && owner != targetId)
                    camera.TrackedBones.Clear();
                return targetDescriptor;
            }
        }

        if (_actorManager.GetGPoseTarget() is not { } native ||
            _bindings.GetActorId(native) is not { } nativeId ||
            !TryResolveExactActor(nativeId, out var exactNative) ||
            !ReferenceEquals(native, exactNative) ||
            ResolveActorDescriptor(nativeId) is not { } nativeDescriptor)
        {
            camera.TrackedBones.Clear();
            return null;
        }
        if (trackedOwner is { } trackedId && trackedId != nativeId)
            camera.TrackedBones.Clear();
        return nativeDescriptor;
    }

    /// <summary>Resolves the exact explicit target, then the current native
    /// game target. Display names never recover identity.</summary>
    private ActorDescriptor? ResolveCameraTrackedActor(IVirtualCamera camera)
    {
        if (camera.TargetActorId is { } targetId)
        {
            if (TryResolveExactActor(targetId, out var target) &&
                ReferenceEquals(camera.TargetActor, target))
                return ResolveActorDescriptor(targetId);
            return null;
        }

        if (_actorManager.GetGPoseTarget() is not { } native ||
            _bindings.GetActorId(native) is not { } nativeId ||
            !TryResolveExactActor(nativeId, out var exactNative) ||
            !ReferenceEquals(native, exactNative))
            return null;
        return ResolveActorDescriptor(nativeId);
    }

    private void PumpCameraBonePicker(
        CameraId cameraId,
        IVirtualCamera camera,
        ActorDescriptor? currentActor)
    {
        if (_cameraBonePickerCamera == cameraId &&
            _cameraBonePickerActor is { } actorId &&
            currentActor?.Id == actorId &&
            ResolveActorDescriptor(actorId) is { } actor)
        {
            _cameraBoneChoices = BuildCameraBoneChoices(actor);
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
        CameraId cameraId, ActorId actorId, IVirtualCamera camera)
    {
        if (!ResolveExactCamera(cameraId, camera) || camera.IsLocked ||
            ReconcileCameraTrackingActor(cameraId, camera)?.Id != actorId ||
            ResolveActorDescriptor(actorId) is not { } actor)
            return;
        _cameraBonePickerCamera = cameraId;
        _cameraBonePickerActor = actorId;
        _cameraBoneChoices = BuildCameraBoneChoices(actor);
        var options = new PickerOptions<global::Poser.UI.BoneChoice>
        {
            Query = CameraBoneSearch,
            Badge = choice => choice.Badge,
        };
        _cameraTrackingBonePicker.OpenMulti(
            $"camera-tracking-bones:{cameraId}:{actorId}",
            ActorNames.Display(actor),
            _cameraBoneChoices,
            choice => choice.Label,
            choice => choice.Key,
            TrackedBoneKeys(camera, actorId),
            (choice, _) => ToggleCameraTrackedBone(
                cameraId, actorId, choice, camera),
            options: in options);
    }

    private void ToggleCameraTrackedBone(
        CameraId cameraId,
        ActorId actorId,
        global::Poser.UI.BoneChoice choice,
        IVirtualCamera camera)
    {
        var boneId = choice.BoneId;
        if (boneId.Skeleton.Actor != actorId || camera.IsLocked ||
            _selection.Primary is not
                { Kind: SceneEntityKind.Camera, Camera: { } selectedCamera }
            || selectedCamera != cameraId ||
            !ResolveExactCamera(cameraId, camera) ||
            ReconcileCameraTrackingActor(cameraId, camera)?.Id != actorId)
            return;
        _cameraPane.ToggleTrackedBone(camera, boneId);
    }

    private bool ResolveExactCamera(CameraId cameraId, IVirtualCamera camera)
    {
        var resolved = _bindings.Resolve(cameraId);
        return resolved.Success && ReferenceEquals(resolved.Value, camera)
            && _bindings.GetCameraId(camera) == cameraId;
    }

    private bool TryResolveExactActor(ActorId actorId, out IActor actor)
    {
        var resolved = _bindings.Resolve(actorId);
        if (resolved.Success && resolved.Value is { } exact &&
            _bindings.GetActorId(exact) == actorId)
        {
            actor = exact;
            return true;
        }
        actor = null!;
        return false;
    }

    private bool ResolveExactActor(ActorId actorId) =>
        TryResolveExactActor(actorId, out _);

    private ActorDescriptor? ResolveActorDescriptor(ActorId actorId) =>
        ResolveExactActor(actorId)
            ? _scene.Snapshot.FindActor(actorId)
            : null;

    private HashSet<string> TrackedBoneKeys(
        IVirtualCamera camera, ActorId actorId) =>
        camera.TrackedBones
            .Select(_bindings.GetBoneId)
            .Where(id => id is { } boneId &&
                boneId.Skeleton.Actor == actorId)
            .Select(id => id!.Value.ToString())
            .ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<global::Poser.UI.BoneChoice> BuildCameraBoneChoices(
        ActorDescriptor actor)
    {
        var rows = new List<global::Poser.UI.BoneChoice>();
        var skeleton = actor.CharacterSkeleton;
        if (skeleton != null)
        {
            var byName = new Dictionary<string,
                (BoneDescriptor Bone, int Ordinal)>(StringComparer.Ordinal);
            int ordinal = 0;
            foreach (var bone in skeleton.Bones)
                if (!bone.IsHidden && !IsBoneSuppressed(bone))
                    byName[bone.Id.CanonicalName] = (bone, ordinal++);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var categories = new List<BuiltCategory>();
            foreach (var root in Core.BoneInfo.KtisisBoneCategories.Roots)
                if (BuildKtisisCategory(
                        root, byName, claimed, string.Empty, filtering: false)
                    is { } category)
                    categories.Add(category);
            var leftovers = byName.Values
                .Where(entry => !claimed.Contains(entry.Bone.Id.CanonicalName))
                .OrderBy(entry => entry.Ordinal)
                .Select(entry => entry.Bone)
                .ToList();
            if (leftovers.Count > 0)
                categories.Add(new BuiltCategory(
                    "Other", "Other", leftovers, leftovers, []));
            foreach (var category in categories)
                AddCameraCategoryBones(rows, category, []);
        }

        foreach (var auxiliary in actor.Skeletons.Where(value =>
            value.Id.Slot != PoseSlot.Character))
        {
            string label = SlotLabel(auxiliary.Id.Slot);
            foreach (var bone in auxiliary.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone))
                    continue;
                rows.Add(new global::Poser.UI.BoneChoice(
                    bone.Id.ToString(),
                    bone.DisplayName,
                    $"{label} {bone.DisplayName} {bone.Id.CanonicalName}",
                    bone.Id,
                    label));
            }
        }
        return rows;
    }

    private static void AddCameraCategoryBones(
        List<global::Poser.UI.BoneChoice> rows,
        BuiltCategory category,
        string[] ancestors)
    {
        var contexts = new string[ancestors.Length + 1];
        Array.Copy(ancestors, contexts, ancestors.Length);
        contexts[^1] = category.Label;
        foreach (var child in category.Children)
            AddCameraCategoryBones(rows, child, contexts);
        string searchContext = string.Join(' ', contexts);
        foreach (var bone in category.VisibleBones)
            rows.Add(new global::Poser.UI.BoneChoice(
                bone.Id.ToString(),
                bone.DisplayName,
                $"{searchContext} {bone.DisplayName} "
                    + bone.Id.CanonicalName,
                bone.Id,
                category.Label));
    }

    private IReadOnlyList<global::Poser.UI.BoneChoice> CameraBoneSearch(string query) =>
        query.Length == 0
            ? _cameraBoneChoices
            : _cameraBoneChoices.Where(choice => choice.SearchText.Contains(
                query, StringComparison.OrdinalIgnoreCase)).ToArray();
}
