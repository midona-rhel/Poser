using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using Poser.Documents.Animation;
using System.Buffers.Binary;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

// Operates only on an independently loaded resource, never a live actor's
// animation control or binding. Native allocations exist only during Save.
internal sealed unsafe class IdleHavokEncoder(IFramework framework, ISigScanner scanner)
{
    internal sealed record SkeletonLayout(string Name, string[] Bones, short[] Parents);

    private static byte[] SkeletonData(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("blks"u8))
            throw new InvalidDataException("Unsupported skeleton file.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int offset = version switch
        {
            0x31333030 or 0x31333031 => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)),
            0x31323030 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)),
            _ => throw new InvalidDataException("Unsupported skeleton version."),
        };
        if (offset < 12 || offset > bytes.Length - 8) throw new InvalidDataException("Invalid skeleton data offset.");
        return bytes[offset..];
    }

    public SkeletonLayout ReadSkeleton(byte[] sklb)
    {
        RequireFramework();
        using var loaded = new LoadedAnimation(SkeletonData(sklb));
        if (loaded.Container->Skeletons.Length != 1 || loaded.Container->Skeletons[0].ptr == null)
            throw new InvalidDataException("Expected one destination skeleton.");
        var skeleton = loaded.Container->Skeletons[0].ptr;
        if (skeleton->Bones.Length is < 1 or > 1024 || skeleton->ParentIndices.Length != skeleton->Bones.Length)
            throw new InvalidDataException("Invalid destination skeleton layout.");
        var names = new string[skeleton->Bones.Length];
        var parents = new short[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = skeleton->Bones[i].Name.String ?? throw new InvalidDataException("Missing bone name.");
            parents[i] = skeleton->ParentIndices[i];
            if (string.IsNullOrEmpty(names[i]) || parents[i] < -1 || parents[i] >= i)
                throw new InvalidDataException("Invalid skeleton bone hierarchy.");
        }
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Ambiguous destination bone names.");
        return new(skeleton->Name.String ?? throw new InvalidDataException("Missing skeleton name."), names, parents);
    }

    public PoseTransform[] SampleStart(byte[] havok, byte[] sklb, int index)
    {
        RequireFramework();
        using var motion = new LoadedAnimation(havok);
        using var rig = new LoadedAnimation(SkeletonData(sklb));
        if (rig.Container->Skeletons.Length != 1 || index < 0 || index >= motion.Container->Bindings.Length)
            throw new InvalidDataException("Missing animation or skeleton binding.");
        var skeleton = rig.Container->Skeletons[0].ptr;
        var binding = motion.Container->Bindings[index].ptr;
        if (skeleton == null || binding == null || binding->Animation.ptr == null ||
            skeleton->Bones.Length is < 1 or > 1024 || skeleton->FloatSlots.Length is < 0 or > 1024 ||
            !SameSkeleton(binding->OriginalSkeletonName.String, skeleton->Name.String))
            throw new InvalidDataException("Animation and skeleton are incompatible.");
        for (int i = 0; i < binding->TransformTrackToBoneIndices.Length; i++)
            if (binding->TransformTrackToBoneIndices[i] < -1 || binding->TransformTrackToBoneIndices[i] >= skeleton->Bones.Length)
                throw new InvalidDataException("Animation references a missing bone.");
        for (int i = 0; i < binding->FloatTrackToFloatSlotIndices.Length; i++)
            if (binding->FloatTrackToFloatSlotIndices[i] < -1 || binding->FloatTrackToFloatSlotIndices[i] >= skeleton->FloatSlots.Length)
                throw new InvalidDataException("Animation references a missing float slot.");
        using var memory = new ExportMemory();
        var animated = memory.New<hkaAnimatedSkeleton>();
        var control = memory.New<hkaAnimationControl>();
        var transforms = memory.New<hkQsTransformf>(skeleton->Bones.Length);
        var floats = memory.New<float>(Math.Max(1, skeleton->FloatSlots.Length));
        animated->Ctor1(skeleton);
        try
        {
            control->Ctor1(binding);
            try
            {
                control->Weight = 1;
                control->LocalTime = 0;
                animated->addAnimationControl(control);
                try { animated->sampleAndCombineAnimations(transforms, floats); }
                finally { animated->removeAnimationControl(control); }
                var result = new PoseTransform[skeleton->Bones.Length];
                for (int i = 0; i < result.Length; i++)
                    result[i] = PoseTransform.CreateChecked(
                        new(transforms[i].Translation.X, transforms[i].Translation.Y, transforms[i].Translation.Z),
                        new(transforms[i].Rotation.X, transforms[i].Rotation.Y, transforms[i].Rotation.Z, transforms[i].Rotation.W),
                        new(transforms[i].Scale.X, transforms[i].Scale.Y, transforms[i].Scale.Z));
                return result;
            }
            // Destruct members but not the caller-owned allocation (deleting flag 0).
            finally { control->VirtDtor(0); }
        }
        finally { animated->Dtor(); }
    }

    private void RequireFramework()
    {
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Animation serialization must run on the framework thread.");
    }

    public byte[] Combine(byte[] body, byte[] face)
    {
        RequireFramework();
        using var first = new LoadedAnimation(body);
        using var second = new LoadedAnimation(face);
        if (first.Container->Bindings.Length != 1 || first.Container->Animations.Length != 1 ||
            second.Container->Bindings.Length != 1 || second.Container->Animations.Length != 1)
            throw new InvalidDataException("The prototype requires single-motion source clips.");
        using var memory = new ExportMemory();
        var animations = first.Container->Animations;
        var bindings = first.Container->Bindings;
        try
        {
            first.Container->Animations = memory.Array(new[] { animations[0], second.Container->Animations[0] });
            first.Container->Bindings = memory.Array(new[] { bindings[0], second.Container->Bindings[0] });
            return Serialize(first);
        }
        finally
        {
            first.Container->Animations = animations;
            first.Container->Bindings = bindings;
        }
    }

    private static byte[] Serialize(LoadedAnimation resource)
    {
        var output = Path.Combine(Path.GetTempPath(), $"poser-idle-{Guid.NewGuid():N}.hkx");
        try
        {
            fixed (byte* className = "hkRootLevelContainer\0"u8)
            {
                var klass = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry()->GetClassByName(className);
                if (klass == null) throw new InvalidOperationException("Havok root class is unavailable.");
                hkOstream stream = default;
                stream.Ctor(output);
                try
                {
                    hkResult result = new() { Result = hkResult.hkResultEnum.Failure };
                    hkSerializeUtil.Save(&result, resource.Root, klass, stream.StreamWriter.ptr, default);
                    if (result.Result != hkResult.hkResultEnum.Success) throw new IOException("Havok animation serialization failed.");
                }
                finally { stream.Dtor(); }
            }
            var bytes = File.ReadAllBytes(output);
            using var check = new LoadedAnimation(bytes);
            if (check.Container->Animations.Length != resource.Container->Animations.Length ||
                check.Container->Bindings.Length != resource.Container->Bindings.Length)
                throw new InvalidDataException("Serialized animation lost container entries.");
            return bytes;
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterleavedAnimation
    {
        public hkaAnimation Animation;
        public hkArray<hkQsTransformf> Transforms;
        public hkArray<float> Floats;
    }

    public byte[] Encode(byte[] template, int bindingIndex, IdleAnimationBinding binding, IdleAnimationClip clip)
    {
        RequireFramework();
        if (clip.Frames.Length < 2 || clip.DurationSeconds <= 0 || !float.IsFinite(clip.DurationSeconds) ||
            binding.BoneIndices.IsDefaultOrEmpty || clip.Frames.Any(f => f.Length != binding.BoneIndices.Length))
            throw new InvalidDataException("Invalid sampled animation.");

        // VFXEditor's interleaved-animation constructor reference: the RIP-relative
        // LEA immediately before this instruction loads the virtual table. Validate
        // that instruction before using its displacement; fail closed on game changes.
        var next = scanner.ScanText("48 89 07 48 8B CD 48 89 77 38");
        if (Marshal.ReadByte(next - 7) != 0x48 || Marshal.ReadByte(next - 6) != 0x8D || Marshal.ReadByte(next - 5) != 0x05)
            throw new InvalidOperationException("Unsupported Havok interleaved constructor.");
        nint vtable = next + Marshal.ReadInt32(next - 4);
        if (vtable < scanner.Module.BaseAddress || vtable >= scanner.Module.BaseAddress + scanner.Module.ModuleMemorySize)
            throw new InvalidOperationException("Havok virtual table is outside the game module.");

        using var resource = new LoadedAnimation(template);
        var container = resource.Container;
        if (bindingIndex < 0 || bindingIndex >= container->Bindings.Length || bindingIndex >= container->Animations.Length)
            throw new InvalidDataException("PAP binding index is outside the Havok container.");
        var originalBinding = container->Bindings[bindingIndex];
        var originalAnimation = container->Animations[bindingIndex];
        if (originalBinding.ptr == null || originalAnimation.ptr == null ||
            originalBinding.ptr->Animation.ptr != originalAnimation.ptr ||
            !SameSkeleton(originalBinding.ptr->OriginalSkeletonName.String, binding.SkeletonName) ||
            originalBinding.ptr->PartitionIndices.Length != 0)
            throw new InvalidDataException("The template binding does not match the captured skeleton, or uses unsupported partitions.");

        using var memory = new ExportMemory();
        var anim = memory.New<InterleavedAnimation>();
        *(nint*)anim = vtable;
        anim->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
        anim->Animation.Duration = clip.DurationSeconds;
        anim->Animation.NumberOfTransformTracks = binding.BoneIndices.Length;
        var transforms = clip.Frames.SelectMany(frame => frame).Select(t =>
        {
            var value = t.Normalized();
            return new hkQsTransformf
            {
                Translation = new() { X = value.Position.X, Y = value.Position.Y, Z = value.Position.Z, W = 1 },
                Rotation = new() { X = value.Rotation.X, Y = value.Rotation.Y, Z = value.Rotation.Z, W = value.Rotation.W },
                Scale = new() { X = value.Scale.X, Y = value.Scale.Y, Z = value.Scale.Z, W = 1 },
            };
        }).ToArray();
        anim->Transforms = memory.Array(transforms);
        var replacement = memory.New<hkaAnimationBinding>();
        *replacement = *originalBinding.ptr;
        replacement->Animation = new() { ptr = (hkaAnimation*)anim };
        replacement->TransformTrackToBoneIndices = memory.Array(binding.BoneIndices.ToArray());
        replacement->FloatTrackToFloatSlotIndices = default;
        replacement->BlendHint.Storage = (sbyte)hkaAnimationBinding.BlendHintEnum.Normal;
        try
        {
            container->Animations[bindingIndex] = new() { ptr = (hkaAnimation*)anim };
            container->Bindings[bindingIndex] = new() { ptr = replacement };
            var bytes = Serialize(resource);
            using var verification = new LoadedAnimation(bytes);
            var check = verification.Container;
            if (check->Animations.Length != container->Animations.Length || check->Bindings.Length != container->Bindings.Length)
                throw new InvalidDataException("Serialized animation lost container entries.");
            var verified = check->Bindings[bindingIndex].ptr;
            if (verified == null || verified->Animation.ptr == null ||
                verified->Animation.ptr->Type != hkaAnimation.AnimationType.InterleavedAnimation ||
                verified->TransformTrackToBoneIndices.Length != binding.BoneIndices.Length)
                throw new InvalidDataException("Serialized animation failed binding verification.");
            var interleaved = (InterleavedAnimation*)verified->Animation.ptr;
            if (interleaved->Transforms.Length != transforms.Length)
                throw new InvalidDataException("Serialized animation lost pose samples.");
            for (int i = 0; i < binding.BoneIndices.Length; i++)
                if (verified->TransformTrackToBoneIndices[i] != binding.BoneIndices[i])
                    throw new InvalidDataException("Serialized bone mapping changed.");
            for (int i = 0; i < transforms.Length; i++)
            {
                var expected = transforms[i];
                if (!new ReadOnlySpan<byte>(&interleaved->Transforms.Data[i], sizeof(hkQsTransformf)).SequenceEqual(
                        new ReadOnlySpan<byte>(&expected, sizeof(hkQsTransformf))))
                    throw new InvalidDataException("Serialized pose samples changed.");
            }
            return bytes;
        }
        finally
        {
            // Restore resource-owned pointers BEFORE releasing the resource. It must
            // never attempt to destroy our borrowed, manually allocated replacements.
            container->Animations[bindingIndex] = originalAnimation;
            container->Bindings[bindingIndex] = originalBinding;
        }
    }

    // Vanilla PAP bindings retain DCC namespaces (e.g. c0801_0:mdl:n_root),
    // while SKLB names are just n_root. Bone maps are validated separately.
    private static bool SameSkeleton(string? binding, string? skeleton) =>
        !string.IsNullOrEmpty(binding) && !string.IsNullOrEmpty(skeleton) &&
        binding[(binding.LastIndexOf(':') + 1)..] == skeleton[(skeleton.LastIndexOf(':') + 1)..];

    private sealed class ExportMemory : IDisposable
    {
        private readonly List<nint> _allocations = [];
        public T* New<T>(int count = 1) where T : unmanaged
        {
            var size = checked((nuint)(sizeof(T) * Math.Max(1, count)));
            var address = (nint)NativeMemory.AlignedAlloc((size + 15) & ~(nuint)15, 16);
            NativeMemory.Clear((void*)address, size);
            _allocations.Add(address);
            return (T*)address;
        }
        public hkArray<T> Array<T>(T[] values) where T : unmanaged
        {
            var ptr = New<T>(values.Length);
            values.AsSpan().CopyTo(new Span<T>(ptr, values.Length));
            return new() { Data = ptr, Length = values.Length, CapacityAndFlags = values.Length | int.MinValue };
        }
        public void Dispose() { foreach (var address in _allocations) NativeMemory.AlignedFree((void*)address); }
    }

    private sealed class LoadedAnimation : IDisposable
    {
        private hkResource* _resource;
        public hkRootLevelContainer* Root { get; }
        public hkaAnimationContainer* Container { get; }
        public LoadedAnimation(byte[] bytes)
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            if (registry == null) throw new InvalidOperationException("Havok registry is unavailable.");
            var options = new hkSerializeUtil.LoadOptions
            {
                ClassNameRegistry = registry->GetClassNameRegistry(),
                TypeInfoRegistry = registry->GetTypeInfoRegistry(),
            };
            hkSerializeUtil.ErrorDetails error = default;
            fixed (byte* data = bytes)
                _resource = hkSerializeUtil.LoadFromBuffer(data, bytes.Length, &error, &options);
            if (_resource == null) throw new InvalidDataException($"Havok could not load the animation ({error.Id.Value}).");
            try
            {
                fixed (byte* name = "hkRootLevelContainer\0"u8)
                    Root = (hkRootLevelContainer*)_resource->GetContentsPointer(name, options.TypeInfoRegistry);
                if (Root == null) throw new InvalidDataException("Animation has no root container.");
                Container = (hkaAnimationContainer*)Root->findObjectByType("hkaAnimationContainer", null);
                if (Container == null) throw new InvalidDataException("Animation has no motion container.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            // Only the resource owns its deserialized graph; do not separately
            // release the animation container or any of its child bindings.
            if (_resource != null) ((hkReferencedObject*)_resource)->RemoveReference();
            _resource = null;
        }
    }
}
