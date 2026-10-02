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
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

public partial class MainWindow
{
    // ── status bar, restated only when its numbers move ─────────────────
    private int _statusActorCount = -1;

    private int _statusBones;

    private int _statusFps = -1;

    private ulong _statusRevision;

    private bool _statusPrimed;

    private ActorId? _statusBoneActor;

    private void BuildStatus(SelectionId? primary)
    {
        int actorCount = _scene.Snapshot.Actors.Count;
        if (actorCount != _statusActorCount)
        {
            _statusActorCount = actorCount;
            _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";
        }

        ActorId? statusActor = primary switch
        {
            { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId,
            // A gaze anchor counts as its owning actor, exactly like a bone.
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeOwner } => gazeOwner,
            _ => null,
        };
        // The bone total moves only with the scene's structure or with which
        // actor is selected — never with the frame.
        if (!_statusPrimed ||
            _statusRevision != _scene.Revision ||
            _statusBoneActor != statusActor)
        {
            _statusPrimed = true;
            _statusRevision = _scene.Revision;
            _statusBoneActor = statusActor;
            int bones = 0;
            if (statusActor is { } owner && _scene.Snapshot.FindActor(owner.LogicalId) is { } descriptor)
                foreach (var skeleton in descriptor.Skeletons)
                    bones += skeleton.Bones.Count;
            _statusBones = bones;
            // Restate the right-hand string with the new count.
            _statusFps = -1;
        }

        int fps = (int)MathF.Round(
            ImGui.GetIO().Framerate, MidpointRounding.AwayFromZero);
        if (fps == _statusFps)
            return;
        _statusFps = fps;
        _vm.StatusRight = _statusBones > 0
            ? $"{_statusBones} bones · {fps} fps"
            : $"{fps} fps";
    }

    /// <summary>Catalog spawns carry their spawn kind's icon; slot
    /// companions keep their kind; owners with a reserved slot get its badge.</summary>
    private TablerIcon SidebarActorIcon(ActorDescriptor actor)
    {
        if (actor.AttachmentKind is { } attachmentKind)
            return attachmentKind switch
            {
                CompanionKind.Companion => TablerIcon.Paw,
                CompanionKind.Mount => TablerIcon.Horse,
                CompanionKind.Ornament => TablerIcon.Diamond,
                _ => TablerIcon.Paw,
            };
        var capabilities = _actorControl.Read(actor.Id);
        var kind = capabilities?.SpawnedKind;
        return kind switch
        {
            CompanionKind.Companion => TablerIcon.Paw,
            CompanionKind.Mount => TablerIcon.Horse,
            CompanionKind.Ornament => TablerIcon.Diamond,
            _ => actor.IsCompanion ? TablerIcon.Paw
                : capabilities?.HasCompanionSlot == true
                    ? TablerIcon.UserWithSlot : TablerIcon.User,
        };
    }

    private string ContentKind(SelectionId? primary) => _properties.ContentKind(primary);
    private string TitleEntity(SelectionId? primary) => _properties.TitleEntity(primary);
    private void BuildTabs(SelectionId? primary)
    {
        _properties.ContentMode = _contentMode;
        _properties.Refresh();
    }
    private void ApplyTabLayout(string tab) => _properties.ApplyTabLayout(tab);
    private void DrawTabContent(Vector2 origin, Vector2 size) => _properties.DrawTabContent(origin, size);
    private void OnTabClicked(int index) => _properties.OnTabClicked(index);
    public void CycleTab(int delta) => _properties.CycleTab(delta);
    private void ResyncTabLayout()
    {
        _properties.ContentMode = _contentMode;
        BuildTabs(_selection.Primary);
        ApplyTabLayout(_contentMode == 2 ? "Scene" : _activeTab);
    }
}
