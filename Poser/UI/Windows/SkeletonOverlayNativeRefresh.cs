using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Viewport;

namespace Poser.UI;

/// <summary>One current-draw transform copied from a validated live slot.</summary>
internal readonly record struct SkeletonOverlayTransform(
    BoneId Id,
    Transform Value);

/// <summary>Observable work performed by the live slot reader.</summary>
internal struct SkeletonOverlayNativeRefreshCounters
{
    public int MatrixRefreshes;
    public int SkeletonResolves;
    public int TransformCopies;
}

internal interface ISkeletonOverlayNativeSource
{
    Matrix4x4? GetSkeletonModelMatrix(BoneId id);
    ISkeleton? ResolveSkeleton(SkeletonId id);
}

/// <summary>
/// Reads one overlay slot through the existing native/binding gates. The
/// matrix query is deliberately first: it is the established draw-phase
/// refresh and cache-registration boundary. Everything after it is guarded so
/// a partial or mismatched live slot fails closed without exposing live
/// skeleton references to the window.
/// </summary>
internal sealed class SkeletonOverlayNativeRefresh
{
    private readonly ISkeletonOverlayNativeSource _source;

    public SkeletonOverlayNativeRefresh(
        ViewportProjection viewport,
        StableBindingRegistry bindings)
        : this(new LiveSource(viewport, bindings))
    {
    }

    internal SkeletonOverlayNativeRefresh(ISkeletonOverlayNativeSource source)
    {
        _source = source;
    }

    public bool TryReadSlot(
        SkeletonDescriptor descriptor,
        IReadOnlyList<int> eligibleIndices,
        List<SkeletonOverlayTransform> destination,
        ref SkeletonOverlayNativeRefreshCounters counters,
        out Matrix4x4 modelMatrix)
    {
        destination.Clear();
        modelMatrix = default;
        if (descriptor.Bones.Count == 0 || eligibleIndices.Count == 0)
            return false;

        counters.MatrixRefreshes++;
        // Keep this call outside the fail-soft boundary. Its framework-thread,
        // exact-binding, native refresh and cache-registration behavior is an
        // existing contract and its established exceptions must remain visible.
        if (_source.GetSkeletonModelMatrix(descriptor.Bones[0].Id)
            is not { } refreshedMatrix)
            return false;

        try
        {
            counters.SkeletonResolves++;
            if (_source.ResolveSkeleton(descriptor.Id) is not { } liveSkeleton
                || !liveSkeleton.IsValid
                || liveSkeleton.Bones.Count != descriptor.Bones.Count)
                return false;

            // Validate every descriptor/live pair before copying any value.
            // Count, order, partial, index and name together are the exact
            // identity guard; a generation or slot mismatch therefore fails
            // closed instead of projecting a stale native wrapper.
            for (int i = 0; i < descriptor.Bones.Count; i++)
            {
                var expected = descriptor.Bones[i].Id;
                var live = liveSkeleton.Bones[i];
                if (live.PartialId != expected.PartialId
                    || live.BoneIndex != expected.BoneIndex
                    || !string.Equals(
                        live.BoneName,
                        expected.CanonicalName,
                        StringComparison.Ordinal))
                    return false;
            }

            for (int i = 0; i < eligibleIndices.Count; i++)
            {
                int index = eligibleIndices[i];
                if ((uint)index >= (uint)descriptor.Bones.Count)
                    return false;
                destination.Add(new SkeletonOverlayTransform(
                    descriptor.Bones[index].Id,
                    liveSkeleton.Bones[index].LastTransform));
                counters.TransformCopies++;
            }

            modelMatrix = refreshedMatrix;
            return true;
        }
        catch
        {
            destination.Clear();
            modelMatrix = default;
            return false;
        }
    }

    private sealed class LiveSource : ISkeletonOverlayNativeSource
    {
        private readonly ViewportProjection _viewport;
        private readonly StableBindingRegistry _bindings;

        public LiveSource(
            ViewportProjection viewport,
            StableBindingRegistry bindings)
        {
            _viewport = viewport;
            _bindings = bindings;
        }

        public Matrix4x4? GetSkeletonModelMatrix(BoneId id) =>
            _viewport.GetSkeletonModelMatrix(id);

        public ISkeleton? ResolveSkeleton(SkeletonId id) =>
            _bindings.ResolveSkeleton(id);
    }
}
