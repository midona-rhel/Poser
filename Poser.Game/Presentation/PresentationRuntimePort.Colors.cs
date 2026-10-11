using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Shader;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Presentation;
using Poser.Game.Core;
using NativeVector = FFXIVClientStructs.FFXIV.Common.Math.Vector4;
using TransformMath = Poser.Domain.Transforms.TransformMath;

namespace Poser.Game.Presentation;

public sealed unsafe partial class PresentationRuntimePort
{
    private readonly IGlamourerPort _integration;
    private bool _colorsDisposed;

    private void EnforceColors(ActorId actor, Owned owned)
    {
        if (owned.ColorsSuspended || owned.Colors.Count == 0) return;
        var buffer = ColorBuffer(actor, out _);
        if (buffer.IsEmpty) return;
        foreach (var (channel, value) in owned.Colors) WriteColor(ref buffer[0], channel, value);
    }

    private Span<CustomizeParameter> ColorBuffer(ActorId actor, out string? detail)
    {
        detail = null;
        if (_colorsDisposed) { detail = "Presentation is stopped."; return default; }
        var character = Resolve(actor, out detail);
        if (character == null) return default;
        var access = _integration.ProbeGlamourerAccess(actor);
        if (!access.CanEdit) { detail = access.Detail ?? "Appearance editing is unavailable."; return default; }
        var human = GPoseObjectTable.AsHuman(BaseFor(character, PresentationModel.Character));
        if (character->GameObject.RenderFlags != 0 || human == null)
        { detail = "The human model is unavailable."; return default; }
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
            if (!TransformMath.IsFinite(value))
                return IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Fail("The shader colour is not readable.");
            values[channel] = value;
        }
        return IntegrationValue<IReadOnlyDictionary<AppearanceColorChannel, Vector4>>.Ok(values);
    }

    public Outcome SetColor(ActorId actor, AppearanceColorChannel channel, Vector4 value)
    {
        if (!Enum.IsDefined(channel) || !TransformMath.IsFinite(AppearanceColorSpace.ToShader(value)))
            return Outcome.Fail("The colour is invalid.");
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return Outcome.Fail(detail!);
        var owned = OwnedFor(actor);
        WriteColor(ref buffer[0], channel, value);
        owned.Colors[channel] = value;
        owned.ColorsSuspended = false;
        return Outcome.Ok();
    }

    public Outcome RestoreColor(ActorId actor, AppearanceColorChannel channel, Vector4 incoming)
    {
        if (!Enum.IsDefined(channel) || !TransformMath.IsFinite(AppearanceColorSpace.ToShader(incoming)))
            return Outcome.Fail("The captured colour is invalid.");
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return Outcome.Fail(detail!);
        WriteColor(ref buffer[0], channel, incoming);
        // Relinquish only after the captured value reaches the current buffer.
        Release(actor, owned => owned.Colors.Remove(channel));
        return Outcome.Ok();
    }

    public void SuspendColors(ActorId actor)
    {
        if (_owned.TryGetValue(actor, out var owned)) owned.ColorsSuspended = true;
    }

    public Outcome RestoreColors(ActorId actor, IReadOnlyDictionary<AppearanceColorChannel, Vector4> captures)
    {
        var buffer = ColorBuffer(actor, out var detail);
        if (buffer.IsEmpty) return Outcome.Fail(detail!);
        foreach (var (channel, value) in captures) WriteColor(ref buffer[0], channel, value);
        Release(actor, owned => owned.Colors.Clear());
        return Outcome.Ok();
    }
}
