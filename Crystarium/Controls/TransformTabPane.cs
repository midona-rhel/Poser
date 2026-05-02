using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Core;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.Files;
using Poser.History;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for transform editing in the properties panel.
/// </summary>
public class TransformTabPane : ITabPane
{
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly IPoseFileService? _poseFileService;
    private readonly TransformWidget _transformWidget;

    // File browsers
    private readonly FileBrowser _importBrowser;
    private readonly FileBrowser _exportBrowser;
    private string _lastPosePath = "";

    // Track bone transform frame-by-frame for incremental deltas
    private IBone? _trackingBone;
    private Transform? _lastFrameTransform;

    // Current entity context (set before Draw)
    private IEntity? _entity;

    public string Name => "Transform";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.ArrowsAlt;

    public TransformTabPane(
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IHistoryService historyService,
        IPoseFileService? poseFileService = null)
    {
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _historyService = historyService;
        _poseFileService = poseFileService;
        _transformWidget = new TransformWidget();
        _transformWidget.OnTransformCommit += OnTransformCommit;

        // Initialize file browsers
        _importBrowser = new FileBrowser("Import Pose", new[] { ".pose" }, isSaveMode: false);
        _exportBrowser = new FileBrowser("Export Pose", new[] { ".pose" }, isSaveMode: true);
        _lastPosePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Sets the entity to display/edit. Call before Draw().
    /// </summary>
    public void SetEntity(IEntity? entity)
    {
        _entity = entity;
    }

    /// <summary>
    /// Whether this tab is enabled for the current entity.
    /// </summary>
    public bool IsEnabled => _entity is ITransformable;

    public void Draw()
    {
        Transform transform;
        bool canEdit;

        if (_entity is IActor actor)
        {
            transform = _posingService.GetEffectiveTransform(actor);
            canEdit = _animationService.IsFrozen(actor);
        }
        else if (_entity is IBone bone)
        {
            transform = (_trackingBone == bone && _lastFrameTransform.HasValue)
                ? _lastFrameTransform.Value
                : bone.Transform;
            canEdit = true;
        }
        else if (_entity is LightEntity)
        {
            transform = _entity.Transform;
            canEdit = true; // Lights are always editable
        }
        else if (_entity is VirtualCameraEntity camera)
        {
            // Camera Transform property maps to PositionOffset + angles
            transform = camera.Transform;
            canEdit = !camera.PositionLocked;
        }
        else if (_entity is ITransformable)
        {
            transform = _entity.Transform;
            canEdit = false;
        }
        else
        {
            // No entity or non-transformable - show disabled dummy UI
            transform = Transform.Identity;
            canEdit = false;
        }

        // Draw widget - when _entity is null, this renders disabled state
        bool isDisabled = _entity == null;
        if (_transformWidget.Draw("transform", ref transform, !canEdit || isDisabled))
        {
            if (_entity is ITransformable)
            {
                ApplyTransform(_entity, transform);
            }
        }
        else
        {
            if (_trackingBone != null)
            {
                _trackingBone = null;
                _lastFrameTransform = null;
            }
        }

        // Draw pose action buttons
        DrawPoseActions();

        // Draw file browsers (modal overlays)
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    private void DrawPoseActions()
    {
        var skeleton = GetCurrentSkeleton();
        var bone = _entity as IBone;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // First row: Flip, Mirror, Reset
        using (var row = Flex.Row(gap: Flex.SmallGap))
        {
            // Flip button (works on selected bone)
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                using (ImRaii.Disabled(bone == null))
                {
                    if (Crystarium.Button("Flip", new ButtonProps { Id = "flip_bone", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (bone != null)
                        {
                            _bonePosingService.FlipBone(bone);
                        }
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(bone != null
                        ? "Flip bone rotation"
                        : "Select a bone to flip");
                }
            });

            // Mirror button (works on skeleton)
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                using (ImRaii.Disabled(skeleton == null))
                {
                    if (Crystarium.Button("Mirror", new ButtonProps { Id = "mirror_pose", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (skeleton != null)
                        {
                            _bonePosingService.MirrorPose(skeleton);
                        }
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(skeleton != null
                        ? "Mirror pose (swap left/right)"
                        : "Select an actor/bone to mirror pose");
                }
            });

            // Reset button
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                bool canReset = bone != null || skeleton != null;
                using (ImRaii.Disabled(!canReset))
                {
                    if (Crystarium.Button("Reset", new ButtonProps { Id = "reset_pose", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (bone != null)
                        {
                            _bonePosingService.ResetBone(bone);
                        }
                        else if (skeleton != null)
                        {
                            _bonePosingService.ResetSkeleton(skeleton);
                        }
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(bone != null
                        ? "Reset bone to original"
                        : skeleton != null
                            ? "Reset skeleton to original"
                            : "Select a bone/skeleton to reset");
                }
            });
        }

        // Second row: Import, Export
        if (_poseFileService != null)
        {
            using var row = Flex.Row(gap: Flex.SmallGap);

            // Import button
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                using (ImRaii.Disabled(skeleton == null))
                {
                    if (Crystarium.Button("Import", new ButtonProps { Id = "import_pose", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (skeleton != null)
                        {
                            _importBrowser.Open(_lastPosePath, path =>
                            {
                                _lastPosePath = System.IO.Path.GetDirectoryName(path) ?? _lastPosePath;
                                _poseFileService.ImportPose(skeleton, path);
                            });
                        }
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(skeleton != null
                        ? "Import pose from .pose file"
                        : "Select an actor to import pose");
                }
            });

            // Export button
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                using (ImRaii.Disabled(skeleton == null))
                {
                    if (Crystarium.Button("Export", new ButtonProps { Id = "export_pose", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (skeleton != null)
                        {
                            _exportBrowser.Open(_lastPosePath, path =>
                            {
                                _lastPosePath = System.IO.Path.GetDirectoryName(path) ?? _lastPosePath;
                                _poseFileService.ExportPose(skeleton, path);
                            });
                        }
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(skeleton != null
                        ? "Export pose to .pose file"
                        : "Select an actor to export pose");
                }
            });
        }
    }

    private ISkeleton? GetCurrentSkeleton()
    {
        if (_entity is ISkeleton skeleton)
            return skeleton;

        if (_entity is IBone bone)
            return bone.Skeleton;

        return null;
    }

    private void ApplyTransform(IEntity entity, Transform transform)
    {
        if (entity is IActor actor)
        {
            _posingService.SetTransformOverride(actor, transform);
        }
        else if (entity is IBone bone)
        {
            if (_trackingBone != bone)
            {
                _trackingBone = bone;
                _lastFrameTransform = bone.Transform;
            }

            var lastObserved = _lastFrameTransform ?? bone.Transform;
            _bonePosingService.ApplyTransform(bone, transform, lastObserved);
            _lastFrameTransform = transform;
        }
        else if (entity is LightEntity light)
        {
            light.Transform = transform;
        }
        else if (entity is VirtualCameraEntity camera)
        {
            camera.Transform = transform;
        }
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        if (_entity is IActor actor)
        {
            var action = new TransformActorAction(_posingService, actor, oldTransform, newTransform);
            _historyService.Push(action);
        }
        else if (_entity is IBone bone)
        {
            var action = new TransformBoneAction(_bonePosingService, bone, oldTransform, newTransform);
            _historyService.Record(action);
        }
    }
}
