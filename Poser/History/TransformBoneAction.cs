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

    public string Description => $"Transform {_bone.Name}";

    public TransformBoneAction(
        IBonePosingService bonePosingService,
        IBone bone,
        Transform oldTransform,
        Transform newTransform)
    {
        _bonePosingService = bonePosingService;
        _bone = bone;
        _oldTransform = oldTransform;
        _newTransform = newTransform;
    }

    public void Execute()
    {
        // Reset and apply the new transform as a delta from identity
        _bonePosingService.ResetBone(_bone);
        _bonePosingService.ApplyTransform(_bone, _newTransform, Transform.Identity);
    }

    public void Undo()
    {
        // Reset and apply the old transform as a delta from identity
        _bonePosingService.ResetBone(_bone);
        _bonePosingService.ApplyTransform(_bone, _oldTransform, Transform.Identity);
    }
}
