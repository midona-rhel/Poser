using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>Capture native state once; all file parsing/skinning works on managed snapshots.</summary>
public sealed class ActorColliderCapture(
    IEntityBindings bindings, IIntegrationRuntimePort integration, IDataManager data,
    ISceneLifecycleHistory lifecycle, IGPoseService gpose, IPluginLog log)
{
    private sealed record ModelSnapshot(string Path, uint Attributes, uint Shapes);
    private sealed record Snapshot(ModelSnapshot[] Models, Dictionary<string, Matrix4x4> Bones,
        Matrix4x4 World, Vector3 Origin);

    public bool Busy { get; private set; }

    public async Task<IOverlayNode> CreateAsync(ActorId actorId, string name)
    {
        if (Busy) throw new InvalidOperationException("An actor collider is already being captured.");
        Busy = true;
        var timer = Stopwatch.StartNew();
        try
        {
            var snapshot = await integration.OnFrameworkThread(() => Capture(actorId));
            var mesh = await Task.Run(() => Build(snapshot));
            return await integration.OnFrameworkThread(() =>
            {
                if (!gpose.IsGPosing || !bindings.Resolve(actorId).Success)
                    throw new InvalidOperationException("The actor left the scene during collider capture.");
                var state = new OverlayNodeState
                {
                    Kind = OverlayNodeKind.Collider,
                    Name = name + " collider",
                    Alpha = .15f,
                    Collider = new IkCollider
                    {
                        Shape = IkColliderShape.Mesh, Mesh = mesh,
                        Transform = new PoseTransform(snapshot.Origin, Quaternion.Identity, Vector3.One),
                    },
                };
                var node = lifecycle.SpawnOverlay(state) as IOverlayNode
                    ?? throw new InvalidOperationException("The collider could not be created.");
                log.Information($"Actor collider captured {snapshot.Models.Length} models, {mesh.Indices.Length / 3} triangles in {timer.ElapsedMilliseconds} ms.");
                return node;
            });
        }
        finally { Busy = false; }
    }

    private unsafe Snapshot Capture(ActorId id)
    {
        if (!gpose.IsGPosing || bindings.Resolve(id).Value?.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            throw new InvalidOperationException("Select an actor with a loaded skeleton in GPose.");
        var character = (CharacterBase*)skeleton.CharacterBaseAddress;
        if (character == null || character->Skeleton == null)
            throw new InvalidOperationException("The actor's model is not loaded.");

        var paths = integration.GetActorResourcePaths(id);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Value is { } resources)
            foreach (var (actual, original) in resources)
                foreach (var path in original) replacements[path] = actual;

        var models = new List<ModelSnapshot>();
        foreach (var entry in character->ModelsSpan)
        {
            var model = entry.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            var path = model->ModelResourceHandle->FileName.ToString();
            // Penumbra's collection discriminator is resource identity, not a disk path.
            path = path[(path.LastIndexOf('|') + 1)..];
            if (replacements.TryGetValue(path, out var resolved)) path = resolved;
            models.Add(new(path, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask));
        }
        if (models.Count == 0) throw new InvalidOperationException("The actor has no loaded meshes.");

        var world = skeleton.GetModelMatrix();
        var bones = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var (bone, reference) in skeleton.CaptureReferencePose())
        {
            var partial = &character->Skeleton->PartialSkeletons[bone.PartialId];
            var pose = partial->GetHavokPose(0);
            if (pose == null) continue;
            var live = pose->AccessBoneModelSpace(bone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
            if (live == null || !Matrix4x4.Invert(reference.ToMatrix(), out var inverseBind)) continue;
            var current = new global::Poser.Transform
            {
                Position = new(live->Translation.X, live->Translation.Y, live->Translation.Z),
                Rotation = new(live->Rotation.X, live->Rotation.Y, live->Rotation.Z, live->Rotation.W),
                Scale = new(live->Scale.X, live->Scale.Y, live->Scale.Z),
            };
            // Row-vector skinning: bind-model -> bone-local -> posed-model -> world.
            // Read the native final pose, not demand-driven inspector caches or raw baselines.
            bones.TryAdd(bone.BoneName, inverseBind * current.ToMatrix() * world);
        }
        return new(models.ToArray(), bones, world, new(world.M41, world.M42, world.M43));
    }

    private IkColliderMesh Build(Snapshot snapshot)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var model in snapshot.Models)
        {
            var bytes = Path.IsPathRooted(model.Path) ? File.ReadAllBytes(model.Path)
                : data.GetFile(model.Path)?.Data ?? throw new IOException("Could not load actor model: " + model.Path);
            ActorColliderMeshBuilder.Append(bytes, model.Attributes, model.Shapes,
                snapshot.Bones, snapshot.World, snapshot.Origin, vertices, indices);
        }
        return new(vertices.ToArray(), indices.ToArray());
    }
}
