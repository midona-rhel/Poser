using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Application.Integration;
using Poser.Application.Scene;
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
    Scene.SceneLifecycleHistory lifecycle, SceneGroups groups, IGPoseService gpose, IPluginLog log)
{
    private sealed record ModelSnapshot(string Path, ushort Race, uint Attributes, uint Shapes);
    private sealed record Snapshot(ModelSnapshot[] Models, Dictionary<string, Matrix4x4> Bones,
        Dictionary<string, ActorBodyColliderBuilder.Joint> Joints,
        Matrix4x4 World, Vector3 Origin, ushort Race, string DeformerPath);

    public bool Busy { get; private set; }

    public async Task<SceneGroup> CreateAsync(ActorId actorId, string name)
    {
        if (Busy) throw new InvalidOperationException("An actor collider is already being captured.");
        Busy = true;
        var timer = Stopwatch.StartNew();
        try
        {
            var snapshot = await integration.OnFrameworkThread(() => Capture(actorId));
            var parts = await Task.Run(() => Build(snapshot));
            return await integration.OnFrameworkThread(() =>
            {
                if (!gpose.IsGPosing || !bindings.Resolve(actorId).Success)
                    throw new InvalidOperationException("The actor left the scene during collider capture.");
                var states = parts.Select(part => new OverlayNodeState
                {
                    Kind = OverlayNodeKind.Collider,
                    Name = part.Name,
                    Alpha = .15f,
                    Collider = part.Collider with
                    {
                        Transform = part.Collider.Transform with { Position = part.Collider.Transform.Position + snapshot.Origin },
                    },
                }).ToArray();
                var group = lifecycle.SpawnOverlayGroup(name + " colliders", states, groups,
                    node => bindings.GetOverlayId((IOverlayNode)node) is { } id ? SelectionId.ForOverlay(id) : null);
                log.Information($"Actor body collider fitted {parts.Length} capsule/sphere parts from {snapshot.Models.Length} models in {timer.ElapsedMilliseconds} ms.");
                return group;
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
            // Hair can also be weighted to the head, so bone ownership alone
            // cannot classify it. Filter the original resource before resolving
            // arbitrary Penumbra disk filenames.
            if (!IsBodyModelPath(path) || resourcesForExcludedModel(path)) continue;
            if (replacements.TryGetValue(path, out var resolved)) path = resolved;
            var raceMatch = Regex.Match(Path.GetFileName(path), "^c([0-9]{4})", RegexOptions.CultureInvariant);
            ushort race = raceMatch.Success ? ushort.Parse(raceMatch.Groups[1].Value) : (ushort)0;
            models.Add(new(path, race, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask));
        }
        if (models.Count == 0) throw new InvalidOperationException("The actor has no loaded meshes.");

        var world = skeleton.GetModelMatrix();
        var origin = new Vector3(world.M41, world.M42, world.M43);
        var bones = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var joints = new Dictionary<string, ActorBodyColliderBuilder.Joint>(StringComparer.Ordinal);
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
            joints.TryAdd(bone.BoneName, new(Vector3.Transform(current.Position, world) - origin, bone.ParentBone?.BoneName));
        }
        ushort skeletonRace = character->GetModelType() == CharacterBase.ModelType.Human ? ((Human*)character)->RaceSexId : (ushort)0;
        var deformerPath = replacements.GetValueOrDefault(ActorColliderDeformation.GamePath, ActorColliderDeformation.GamePath);
        return new(models.ToArray(), bones, joints, world, origin, skeletonRace, deformerPath);

        bool resourcesForExcludedModel(string actual) => paths.Value is { } resourcePaths &&
            resourcePaths.TryGetValue(actual, out var originals) && originals.Any(p => !IsBodyModelPath(p));
    }

    internal static bool IsBodyModelPath(string path) =>
        !Regex.IsMatch(path.Replace('\\', '/'), @"(?:/hair/|/tail/|_hir\.mdl$|_til\.mdl$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private ActorBodyColliderBuilder.Fitted[] Build(Snapshot snapshot)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var influences = new List<string?>();
        var bodyBones = ActorBodyColliderBuilder.BodyBones(snapshot.Joints);
        byte[] ReadFile(string path) => Path.IsPathRooted(path) ? File.ReadAllBytes(path)
            : data.GetFile(path)?.Data ?? throw new IOException("Could not load actor resource: " + path);
        byte[]? pbd = null;
        var raceBones = new Dictionary<ushort, Dictionary<string, Matrix4x4>>();
        foreach (var model in snapshot.Models)
        {
            var bytes = ReadFile(model.Path);
            try
            {
                var bones = snapshot.Bones;
                if (snapshot.Race != 0 && model.Race != 0 && snapshot.Race != model.Race)
                {
                    if (!raceBones.TryGetValue(model.Race, out bones))
                    {
                        pbd ??= ReadFile(snapshot.DeformerPath);
                        var deformer = ActorColliderDeformation.Read(pbd, snapshot.Race, model.Race);
                        bones = snapshot.Bones.ToDictionary(b => b.Key,
                            b => deformer.TryGetValue(b.Key, out var deform) ? deform * b.Value : b.Value);
                        raceBones.Add(model.Race, bones);
                    }
                }
                ActorColliderMeshBuilder.Append(bytes, model.Attributes, model.Shapes,
                    bones, snapshot.World, snapshot.Origin, vertices, indices, influences, bodyBones);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"{Path.GetFileName(model.Path)}: {ex.Message}", ex);
            }
        }
        return ActorBodyColliderBuilder.Fit(snapshot.Joints, vertices, indices, influences);
    }
}
