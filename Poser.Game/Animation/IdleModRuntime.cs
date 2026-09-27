using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Application.Integration;
using Poser.Application.Posing;
using Poser.Documents.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Animation;

public sealed class IdleModRuntime(
    IFramework framework, IDataManager data, ISigScanner scanner,
    StableBindingRegistry bindings, ISkeletonService skeletons, PoseExportCapture capture,
    IPoseImportCommands imports, IIntegrationRuntimePort integration) : IIdleModRuntime
{
    private readonly IdleHavokEncoder _encoder = new(framework, scanner);

    public async Task<IdleModPackage> CaptureAsync(ActorId actorId)
    {
        var completion = new TaskCompletionSource<IdleModPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await framework.RunOnFrameworkThread(() =>
        {
            if (imports.IsImportBusy) throw new InvalidOperationException("Wait for the pose import to finish.");
            var actor = bindings.Resolve(actorId).Value ?? throw new InvalidOperationException("The actor is unavailable.");
            var slots = skeletons.GetSkeletons(actor);
            var character = slots.SingleOrDefault(s => s.Slot == PoseSlot.Character)
                ?? throw new InvalidOperationException("The actor has no character skeleton.");
            var revision = character.BuildRevision;
            Exception? error = null;
            var started = capture.Begin(slots, _ =>
            {
                try
                {
                    if (!ReferenceEquals(bindings.Resolve(actorId).Value, actor) || !character.IsValid || character.BuildRevision != revision)
                        throw new InvalidOperationException("The actor changed during idle capture.");
                    completion.TrySetResult(Build(actorId, actor.Name, character));
                    return true;
                }
                catch (Exception ex) { error = ex; return false; }
            }, ok =>
            {
                if (!ok) completion.TrySetException(error ?? new InvalidOperationException("The pose did not finish refreshing."));
            });
            if (!started.Success) throw new InvalidOperationException(started.Detail);
        });
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private unsafe IdleModPackage Build(ActorId id, string name, ISkeleton skeleton)
    {
        var model = (CharacterBase*)skeleton.CharacterBaseAddress;
        if (model == null || model->GetModelType() != CharacterBase.ModelType.Human || model->Skeleton == null)
            throw new InvalidOperationException("Idle export currently supports humanoid actors only.");
        var race = ((Human*)model)->RaceSexId;
        string raceName = $"c{race:0000}";
        var paths = integration.GetActorResourcePaths(id);
        var facePaths = paths.Value?.Values.SelectMany(p => p)
            .Where(p => Regex.IsMatch(p, $@"^chara/human/{raceName}/skeleton/face/f[0-9]{{4}}/skl_{raceName}f[0-9]{{4}}\.sklb$"))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (facePaths is not { Length: 1 })
            throw new InvalidOperationException("Could not determine this actor's exact face skeleton from Penumbra resources.");
        string facePath = facePaths[0];
        string face = facePath.Split('/')[5];
        string basePath = $"chara/human/{raceName}/skeleton/base/b0001/skl_{raceName}b0001.sklb";
        byte[] Read(string path) => data.GetFile(path)?.Data ?? throw new FileNotFoundException("Unsupported idle resource: " + path);
        var bodySkeleton = Read(basePath);
        var faceSkeleton = Read(facePath);
        byte[] ReadLoaded(string path)
        {
            var resolved = paths.Value!.Where(entry => entry.Value.Contains(path, StringComparer.Ordinal))
                .Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (resolved.Length != 1) throw new InvalidOperationException("The loaded skeleton path is ambiguous: " + path);
            return Path.IsPathFullyQualified(resolved[0]) ? File.ReadAllBytes(resolved[0]) : Read(resolved[0]);
        }
        var vanillaBodyLayout = _encoder.ReadSkeleton(bodySkeleton);
        var vanillaFaceLayout = _encoder.ReadSkeleton(faceSkeleton);
        var bodyLayout = _encoder.ReadSkeleton(ReadLoaded(basePath));
        var faceLayout = _encoder.ReadSkeleton(ReadLoaded(facePath));
        string prefix = $"chara/human/{raceName}/animation/a0001/bt_common/emote/pose01_";
        var start = new PapAnimationDocument(Read(prefix + "start.pap"));
        var loop = new PapAnimationDocument(Read(prefix + "loop.pap"));
        var faceTemplate = new PapAnimationDocument(Read($"chara/human/{raceName}/animation/{face}/nonresident/emot/joy.pap"));
        var neutral = new PapAnimationDocument(Read($"chara/human/{raceName}/animation/{face}/resident/face.pap"));
        if (start.Clips.Count != 1 || loop.Clips.Count != 1 || faceTemplate.Clips.Count != 1)
            throw new InvalidDataException("This idle's animation layout is not supported yet.");
        int neutralIndex = neutral.Clips.Single(c => c.Name == "cfxf_base").BindingIndex;
        var bodyIdle = _encoder.SampleStart(start.HavokBytes, bodySkeleton, start.Clips[0].BindingIndex);
        var faceIdle = _encoder.SampleStart(neutral.HavokBytes, faceSkeleton, neutralIndex);
        var facePartial = skeleton.Bones.Where(b => b.BoneName.StartsWith("j_f_", StringComparison.Ordinal))
            .Select(b => b.PartialId).Distinct().ToArray();
        if (facePartial.Length != 1) throw new InvalidOperationException("The actor does not have one identifiable face skeleton.");
        var bodyPose = Tracks(bodyLayout, RemapIdle(vanillaBodyLayout, bodyIdle, bodyLayout), 0);
        var expression = Tracks(faceLayout, RemapIdle(vanillaFaceLayout, faceIdle, faceLayout), facePartial[0]);
        // Standing pose 1 has a 45-frame entry and 70-frame loop in the game.
        // Keep its native timelines: no shared ActionTimeline edits or custom exit.
        var samples = IdleAnimationSamples.Create(bodyPose, expression, 45f / 30f);
        var bodyEntry = _encoder.Encode(start.HavokBytes, 0, samples.Body, samples.Body.Entry);
        var faceEntry = _encoder.Encode(faceTemplate.HavokBytes, 0, samples.Expression, samples.Expression.Entry);
        var bodyHold = _encoder.Encode(loop.HavokBytes, 0, samples.Body, samples.Body.Hold with { DurationSeconds = 70f / 30f });
        var faceHold = _encoder.Encode(faceTemplate.HavokBytes, 0, samples.Expression, samples.Expression.Hold with { DurationSeconds = 70f / 30f });
        return new(name + " static pose", $"Experimental standing pose 1 replacement for {raceName}, face {face}. " +
            "Body/IK and facial pose are baked. Use only in this character's Penumbra collection with the same appearance and Customize+ profile. " +
            "Requires the same body and face skeleton mods. Equipment, hair/cloth partials and gaze tracking are not exported. Entry is sine-eased; exit uses the game's normal blend-out.",
            [new(prefix + "start.pap", IdlePapBuilder.BuildPair(start, _encoder.Combine(bodyEntry, faceEntry), 45)),
             new(prefix + "loop.pap", IdlePapBuilder.BuildPair(loop, _encoder.Combine(bodyHold, faceHold), 70))]);

        IdleSkeletonPose Tracks(IdleHavokEncoder.SkeletonLayout layout, PoseTransform[] idle, int partial)
        {
            var source = skeleton.Bones.Where(b => b.PartialId == partial).ToDictionary(b => b.BoneName, StringComparer.Ordinal);
            if (!source.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(layout.Bones))
                throw new InvalidOperationException("The actor's loaded skeleton changed; redraw it before exporting.");
            var tracks = ImmutableArray.CreateBuilder<IdleBoneTrack>(layout.Bones.Length);
            for (short i = 0; i < layout.Bones.Length; i++)
            {
                if (!source.TryGetValue(layout.Bones[i], out var bone))
                    throw new InvalidOperationException("The actor is missing required bone " + layout.Bones[i]);
                // LastRawTransform is the refreshed update-phase pose BEFORE
                // render-phase Customize+ changes; reusing it avoids baking C+ twice.
                var local = bone.LastRawTransform.ToMatrix();
                if (layout.Parents[i] >= 0)
                {
                    if (!source.TryGetValue(layout.Bones[layout.Parents[i]], out var parent) ||
                        !Matrix4x4.Invert(parent.LastRawTransform.ToMatrix(), out var inverse))
                        throw new InvalidOperationException("The pose contains a singular parent transform.");
                    local *= inverse;
                }
                else if (partial != 0)
                {
                    // Facial partial roots are attached by the game, not independent
                    // world/model tracks. Preserve the clip's root rather than the
                    // already reparented head placement from the live skeleton.
                    tracks.Add(new(i, idle[i], idle[i]));
                    continue;
                }
                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position))
                    throw new InvalidOperationException("The pose contains a local transform that cannot be represented in animation.");
                tracks.Add(new(i, idle[i], PoseTransform.CreateChecked(position, rotation, scale)));
            }
            return new(layout.Name, layout.Bones.Length, tracks.MoveToImmutable());
        }
    }

    private static PoseTransform[] RemapIdle(IdleHavokEncoder.SkeletonLayout vanilla, PoseTransform[] idle,
        IdleHavokEncoder.SkeletonLayout destination)
    {
        var source = vanilla.Bones.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index);
        return destination.Bones.Select((name, i) =>
        {
            if (!source.TryGetValue(name, out int original)) return destination.ReferencePose[i];
            string? Parent(IdleHavokEncoder.SkeletonLayout layout, int bone) =>
                layout.Parents[bone] < 0 ? null : layout.Bones[layout.Parents[bone]];
            if (Parent(vanilla, original) != Parent(destination, i))
                throw new InvalidOperationException("The custom skeleton reparents a standard bone; this idle layout is not supported.");
            return idle[original];
        }).ToArray();
    }
}
