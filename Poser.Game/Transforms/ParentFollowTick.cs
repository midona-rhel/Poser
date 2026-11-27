using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Application.Lifecycle;
using Poser.Game.Services;

namespace Poser.Game.Transforms;

/// <summary>Moves every child to follow its parent each frame, after the game
/// updates the scene and again before IK collisions run. The parent links
/// themselves are kept in <see cref="TransformParenting"/>.</summary>
public sealed class ParentFollowTick : IDisposable
{
    private readonly TransformParenting _parents;
    private readonly Runtime.SceneFramePhaseService _phases;
    private readonly IKService _ik;
    private readonly IFramework _framework;
    private readonly IGPoseService _gpose;
    private readonly ILightingService _lights;
    private readonly IEntityBindings _bindings;

    public ParentFollowTick(TransformParenting parents, Runtime.SceneFramePhaseService phases,
        IKService ik, IFramework framework, IGPoseService gpose, ILightingService lights, IEntityBindings bindings)
    {
        _parents = parents; _phases = phases; _ik = ik; _framework = framework; _gpose = gpose;
        _lights = lights; _bindings = bindings;
        phases.TransformUpdate += Tick;
        ik.BeforeCollisions += Tick;
        framework.Update += Framework;
    }

    private void Framework(IFramework _) { if (!_gpose.IsGPosing) _parents.Clear(); }
    private void Tick()
    {
        if (!_gpose.IsGPosing) return;
        // A light attached to a bone by an older scene becomes a parent link
        // with no offset, once; from then on the link moves it.
        foreach (var light in _lights.Lights)
            if (light.AttachedBone is { } bone && _bindings.GetLightId(light) is { } id
                && _bindings.GetBoneId(bone) is { } boneId)
            {
                var child = SelectionId.ForLight(id);
                if (_parents.Read(child) == null)
                    _parents.Import(child, new(SelectionId.ForBone(boneId), PoseTransform.Identity with { Scale = light.Transform.Scale }));
                light.AttachedBone = null;
            }
        _parents.Evaluate();
    }
    public void Dispose()
    {
        _phases.TransformUpdate -= Tick; _ik.BeforeCollisions -= Tick;
        _framework.Update -= Framework; _parents.Clear();
    }
}
