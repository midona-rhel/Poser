using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.UI;

public partial class PoseInspectorPane
{
    private static readonly string[] FabrikDirections = ["Forward", "Reverse", "Bidirectional"];
    private bool _fabrikRoot;
    private (TransformTargetId Target, bool Root, IkTargetMode Mode)? _fabrikPicking;
    private void DrawFabrikTargets(Crystarium.FormScope form, BoneId endpoint,
        TransformTargetId target, IkChainConfig config)
    {
        var control = config.Fabrik!;
        if (config.FabrikMode != FabrikControlMode.Bidirectional)
            _fabrikRoot = config.FabrikMode == FabrikControlMode.Reverse;
        bool root = _fabrikRoot;
        form.Dropdown("Edit end", ["Root", "Tip"], root ? 0 : 1,
            next => _fabrikRoot = next == 0,
            disabled: config.FabrikMode != FabrikControlMode.Bidirectional);
        var point = root ? control.Root : control.Tip;
        var shownMode = _fabrikPicking is { } pick && pick.Target == target && pick.Root == root
            ? pick.Mode : point.Mode;
        void Adjust(FabrikTarget next) => _ikPort.Adjust(target, config with
            { Fabrik = root ? control with { Root = next } : control with { Tip = next } });
        form.Dropdown("Target", TargetModeItems, (int)shownMode, next =>
        {
            var mode = (IkTargetMode)next;
            if (mode is IkTargetMode.Bone or IkTargetMode.Entity)
            {
                _fabrikPicking = (target, root, mode);
            }
            else
            {
                _fabrikPicking = null;
                if (_ikPort.SetFabrikTarget(target, root, mode) is { Success: false } failed)
                    _notices.Failed($"IK target: {failed.Detail}");
            }
        }, help: "Actor coordinates, a world point, or an offset from a bone or scene entity");
        if (shownMode == IkTargetMode.Bone) DrawIkBoneTarget(form, endpoint, target, root);
        else if (shownMode == IkTargetMode.Entity) DrawIkEntityTarget(form, target, root);
        form.AxisVector(point.Mode is IkTargetMode.Bone or IkTargetMode.Entity ? "Offset" : "Position",
            point.Position, value => Adjust(point with { Position = value }), null, .001f, "0.000",
            help: "Move this endpoint; the chain retains its link lengths");
        form.Switch("Keep rotation", point.HoldRotation,
            value => _ikPort.Set(target, config with { Fabrik = root
                ? control with { Root = point with { HoldRotation = value } }
                : control with { Tip = point with { HoldRotation = value } } }));
    }

    public ContextMenuItem[] FabrikDirectionMenu(BoneId endpoint, out List<Action?> actions)
    {
        var target = TransformTargetId.ForBone(endpoint);
        var config = _ikPort.Get(target);
        actions = new();
        if (config?.Solver != IkSolver.Fabrik) return Array.Empty<ContextMenuItem>();
        var items = new List<ContextMenuItem>();
        foreach (var mode in Enum.GetValues<FabrikControlMode>())
        {
            items.Add(new ContextMenuItem(mode.ToString(), TablerIcon.Rotate));
            actions.Add(() =>
            {
                _fabrikPicking = null;
                if (_ikPort.SetFabrikDirection(target, mode)
                        is { Success: false } failed) _notices.Failed($"IK: {failed.Detail}");
            });
        }
        return items.ToArray();
    }
}
