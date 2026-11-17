using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using static Poser.UI.Widgets.PageForm;

namespace Poser.UI;

public partial class PoseInspectorPane
{
    // Bone or Scene entity was chosen in the Target dropdown and nothing is picked yet.
    private (TransformTargetId Target, IkTargetMode Mode)? _targetPicking;

    /// <summary>The Target dropdown and, under it, the bone or scene entity to follow.
    /// Choosing Bone or Scene entity only shows the picker: the chain keeps its target,
    /// and the limb stays where it is, until something is picked.</summary>
    private void DrawIkTarget(FormScope form, BoneId endpoint, TransformTargetId target,
        IkTargetMode current, bool fabrik, Action<IkTargetMode> set)
    {
        var shownMode = _targetPicking is { } pick && pick.Target == target ? pick.Mode : current;
        form.Dropdown("Target", TargetModeItems, (int)shownMode, next =>
        {
            var mode = (IkTargetMode)next;
            if (mode is IkTargetMode.Bone or IkTargetMode.Entity)
                _targetPicking = (target, mode);
            else
            {
                _targetPicking = null;
                set(mode);
            }
        }, help: "Actor follows the actor; World holds a point; Bone or Scene entity follows the chosen target");
        if (shownMode == IkTargetMode.Bone) DrawIkBoneTarget(form, endpoint, target, fabrik);
        else if (shownMode == IkTargetMode.Entity) DrawIkEntityTarget(form, target, fabrik);
    }

    private void DrawFabrikTargets(FormScope form, BoneId endpoint,
        TransformTargetId target, IkChainConfig config)
    {
        var point = config.Fabrik!.Handle;
        DrawIkTarget(form, endpoint, target, point.Mode, fabrik: true,
            mode => _notices.Report(_ikPort.SetFabrikTarget(target, mode), "IK ▸ Target"));
        form.Switch("Keep rotation", config.HoldRotation,
            value => _ikPort.Set(target, config with { HoldRotation = value }),
            disabled: point.Mode == IkTargetMode.Actor);
    }
}
