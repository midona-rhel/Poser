using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Shader;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using NativeVector = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

namespace Poser.Game.Presentation;

public sealed unsafe partial class PresentationRuntimePort
{
    private readonly IIntegrationRuntimePort _integration;
    private readonly ICallGateSubscriber<nint, int, object?> _redrawn;
    private readonly ColorReleaseCoordinator _colorRelease;
    private bool _colorsDisposed;

    private ColorIntent? ColorIntentFor(ActorId actor) =>
        _owned.TryGetValue(actor, out var owned)
            ? new(owned.ColorRevision, owned.ColorsSuspended, owned.Colors) : null;

    private ColorTarget? ResolveColorTarget(ActorId actor)
    {
        var character = Resolve(actor, out _);
        return character == null ? null : new((nint)character, character->GameObject.ObjectIndex);
    }

    private ColorInspection InspectColor(ActorId actor)
    {
        var target = ResolveColorTarget(actor);
        if (target is null) return new(null, false, false, "The actor is no longer available.");
        var access = _integration.ProbeGlamourerAccess(actor);
        if (!access.CanEdit) return new(target, false, false, access.Detail);
        var buffer = ColorBuffer(actor, out var detail, probeAccess: false);
        bool readable = !buffer.IsEmpty;
        if (readable)
            foreach (var channel in Enum.GetValues<AppearanceColorChannel>())
                readable &= AppearanceColorSpace.IsFinite(ReadColor(ref buffer[0], channel));
        return new(target, true, readable, detail);
    }

    private void ReleaseColor(ActorId actor, AppearanceColorChannel channel)
    {
        var owned = _owned[actor];
        owned.Colors.Remove(channel);
        owned.ColorRevision++;
    }

    private void EnforceInspectedColors(ActorId actor, ColorTarget target,
        IReadOnlyDictionary<AppearanceColorChannel, Vector4> values)
    {
        if (ResolveColorTarget(actor) != target) return;
        // Called only within the coordinator's freshly inspected operation.
        // This resolves the buffer again, but never repeats the same IPC probe.
        var buffer = ColorBuffer(actor, out _, probeAccess: false);
        if (buffer.IsEmpty) return;
        foreach (var (channel, value) in values) WriteColor(ref buffer[0], channel, value);
    }

    private Span<CustomizeParameter> ColorBuffer(ActorId actor, out string? detail, bool probeAccess = true)
    {
        detail = null;
        if (_colorsDisposed) { detail = "Presentation is stopped."; return default; }
        var character = Resolve(actor, out detail);
        if (character == null) return default;
        if (probeAccess)
        {
            var access = _integration.ProbeGlamourerAccess(actor);
            if (!access.CanEdit) { detail = access.Detail ?? "Appearance editing is unavailable."; return default; }
        }
        var model = BaseFor(character, PresentationModel.Character);
        if (character->GameObject.RenderFlags != 0 || model == null
            || model->GetModelType() != CharacterBase.ModelType.Human)
        { detail = "The human model is unavailable."; return default; }
        var human = (Human*)model;
        // DrawObject.NotifyTransformChanged gates UpdateTransforms on LoadState==3
        // (also verified in the installed DLL). This prerequisite alone does not
        // prove shader readiness: require the readable cbuffer too. Allocators may
        // reuse pointers. ConstantBufferPointer<T>.TryGetBuffer has a reversed null
        // branch in the installed DLL, so call the non-null ConstantBuffer directly.
        if (human->LoadState != 3 || human->CustomizeParameterCBuffer == null)
        { detail = "The human shader is still loading."; return default; }
        var buffer = human->CustomizeParameterCBuffer->TryGetBuffer<CustomizeParameter>();
        if (buffer.IsEmpty) detail = "The shader parameter buffer is not readable yet.";
        return buffer;
    }

    private static ref NativeVector Lane(ref CustomizeParameter data, AppearanceColorChannel channel)
    {
        switch (channel)
        {
            case AppearanceColorChannel.Skin: return ref data.SkinColor;
            case AppearanceColorChannel.LeftEye: return ref data.LeftColor;
            case AppearanceColorChannel.RightEye: return ref data.RightColor;
            case AppearanceColorChannel.Mouth: return ref data.LipColor;
            default: throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    internal static Vector4 ReadColor(ref CustomizeParameter data, AppearanceColorChannel channel)
    {
        if (channel is AppearanceColorChannel.Hair or AppearanceColorChannel.Highlights or AppearanceColorChannel.Feature)
        {
            var rgb = channel == AppearanceColorChannel.Hair ? data.MainColor
                : channel == AppearanceColorChannel.Highlights ? data.MeshColor : data.OptionColor;
            return AppearanceColorSpace.FromShader(new(rgb.X, rgb.Y, rgb.Z, 1f));
        }
        ref var lane = ref Lane(ref data, channel);
        return AppearanceColorSpace.FromShader(new(lane.X, lane.Y, lane.Z, lane.W));
    }

    internal static void WriteColor(ref CustomizeParameter data, AppearanceColorChannel channel, Vector4 value)
    {
        var shader = AppearanceColorSpace.ToShader(value);
        if (channel is AppearanceColorChannel.Hair or AppearanceColorChannel.Highlights or AppearanceColorChannel.Feature)
        {
            var rgb = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(shader.X, shader.Y, shader.Z);
            if (channel == AppearanceColorChannel.Hair) data.MainColor = rgb;
            else if (channel == AppearanceColorChannel.Highlights) data.MeshColor = rgb;
            else data.OptionColor = rgb;
            return;
        }
        ref var lane = ref Lane(ref data, channel);
        lane.X = shader.X; lane.Y = shader.Y; lane.Z = shader.Z;
        // Skin W is muscle tone; other RGB W lanes are unrelated shader data.
        // Mouth opacity is linear, unlike the signed-square RGB channels.
        if (channel == AppearanceColorChannel.Mouth) lane.W = shader.W;
    }

    public IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>> ReadColors(ActorId actor)
    {
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Fail(detail!);
        var values = new Dictionary<AppearanceColorChannel, Vector4>();
        foreach (var channel in Enum.GetValues<AppearanceColorChannel>())
        {
            var value = ReadColor(ref buffer[0], channel);
            if (!AppearanceColorSpace.IsFinite(value))
                return IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Fail("The shader colour is not readable.");
            values[channel] = value;
        }
        return IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Ok(values);
    }

    public PresentationPortResult SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
    {
        if (_colorRelease.IsPending(actor)) return PresentationPortResult.Fail("A colour reset is pending for this actor.");
        if (!Enum.IsDefined(channel) || !AppearanceColorSpace.IsFinite(AppearanceColorSpace.ToShader(value)))
            return PresentationPortResult.Fail("The colour is invalid.");
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return PresentationPortResult.Fail(detail!);
        var owned = OwnedFor(actor);
        WriteColor(ref buffer[0], channel, value);
        owned.Colors[channel] = value;
        owned.ColorsSuspended = false;
        owned.ColorRevision++;
        return PresentationPortResult.Ok();
    }

    public void BeginClearColor(ActorId actor, AppearanceColorChannel channel,
        Func<Action, PresentationPortResult> commit, Action<PresentationPortResult> completed) =>
        _colorRelease.Begin(actor, channel, commit, completed);

    private void OnColorRedrawn(nint address, int index)
    {
        if (_framework.IsInFrameworkUpdateThread && !_colorsDisposed)
            _colorRelease.Redrawn(address, index);
    }

    public void SuspendColors(ActorId actor)
    {
        if (_owned.TryGetValue(actor, out var owned)) { owned.ColorsSuspended = true; owned.ColorRevision++; }
        _colorRelease.Cancel(actor);
    }

    public PresentationPortResult RestoreColors(ActorId actor, IReadOnlyDictionary<AppearanceColorChannel, Vector4> captures)
    {
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return PresentationPortResult.Fail(detail!);
        foreach (var (channel, value) in captures) WriteColor(ref buffer[0], channel, value);
        Release(actor, owned => owned.Colors.Clear());
        return PresentationPortResult.Ok();
    }
}
