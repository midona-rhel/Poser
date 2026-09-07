using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.UI;

public partial class PoseInspectorPane
{
    private (TransformTargetId Target, IkTargetMode Mode)? _fabrikPicking;

    private void DrawFabrikTargets(Crystarium.FormScope form, BoneId endpoint,
        TransformTargetId target, IkChainConfig config)
    {
        var point = config.Fabrik!.Handle;
        var shownMode = _fabrikPicking is { } pick && pick.Target == target
            ? pick.Mode : point.Mode;
        form.Dropdown("Target", TargetModeItems, (int)shownMode, next =>
        {
            var mode = (IkTargetMode)next;
            if (mode is IkTargetMode.Bone or IkTargetMode.Entity)
                _fabrikPicking = (target, mode);
            else
            {
                _fabrikPicking = null;
                if (_ikPort.SetFabrikTarget(target, mode) is { Success: false } failed)
                    _notices.Failed($"IK target: {failed.Detail}");
            }
        }, help: "The selected bone follows the actor, a world point, a bone or a scene entity");
        if (shownMode == IkTargetMode.Bone) DrawIkBoneTarget(form, endpoint, target, fabrik: true);
        else if (shownMode == IkTargetMode.Entity) DrawIkEntityTarget(form, target, fabrik: true);
        form.Switch("Keep rotation", config.HoldRotation,
            value => _ikPort.Set(target, config with { HoldRotation = value }),
            disabled: point.Mode == IkTargetMode.Actor);
    }
}
