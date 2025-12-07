using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.History;

/// <summary>
/// Action to transform a bone with undo/redo support.
/// </summary>
public class TransformBoneAction : IHistoryAction
{
    private readonly IBonePosingService _bonePosingService;
    private readonly IBone _bone;
    private readonly Transform _oldTransform;
    private readonly Transform _newTransform;
    private readonly TransformComponents _propagate;

    public string Description => $"Transform {_bone.Name}";

    public TransformBoneAction(
        IBonePosingService bonePosingService,
        IBone bone,
        Transform oldTransform,
        Transform newTransform,
        TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation)
    {
        _bonePosingService = bonePosingService;
        _bone = bone;
        _oldTransform = oldTransform;
        _newTransform = newTransform;
        _propagate = propagate;
    }

    public void Execute()
    {
        _bonePosingService.ApplyTransform(_bone, _newTransform, _oldTransform, _propagate);
    }

    public void Undo()
    {
        _bonePosingService.ApplyTransform(_bone, _oldTransform, _newTransform, _propagate);
    }
}

/// <summary>
/// Action to reset a bone's pose with undo/redo support.
/// </summary>
public class ResetBoneAction : IHistoryAction
{
    private readonly IBonePosingService _bonePosingService;
    private readonly IBone _bone;
    private readonly Transform? _previousModification;

    public string Description => $"Reset {_bone.Name}";

    public ResetBoneAction(
        IBonePosingService bonePosingService,
        IBone bone)
    {
        _bonePosingService = bonePosingService;
        _bone = bone;
        _previousModification = bonePosingService.GetModification(bone);
    }

    public void Execute()
    {
        _bonePosingService.ResetBone(_bone);
    }

    public void Undo()
    {
        if (_previousModification.HasValue)
        {
            _bonePosingService.ApplyTransform(_bone, _previousModification.Value, Transform.Identity);
        }
    }
}
