using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Services;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Transforms;

/// <summary>Frame ownership only. Relationship policy remains in Application.</summary>
public sealed class ParentingFrameRuntime : IDisposable
{
    private readonly TransformParenting _parents;
    private readonly Runtime.SceneFramePhaseService _phases;
    private readonly IKService _ik;
    private readonly IFramework _framework;
    private readonly IGPoseService _gpose;
    private readonly ILightingService _lights;
    private readonly IEntityBindings _bindings;

    public ParentingFrameRuntime(TransformParenting parents, Runtime.SceneFramePhaseService phases,
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
        // Legacy scene attachments have zero offset. Consume once; the graph owns following.
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
