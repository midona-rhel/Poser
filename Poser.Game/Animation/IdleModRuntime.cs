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

    private static readonly string[] RaceNames = ["Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera"];
    public Task<IdleModChoices> DescribeAsync(ActorId actorId) => framework.RunOnFrameworkThread(() =>
    {
        var actor = bindings.Resolve(actorId).Value ?? throw new InvalidOperationException("The actor is unavailable.");
        var skeleton = skeletons.GetSkeletons(actor).SingleOrDefault(s => s.Slot == PoseSlot.Character)
            ?? throw new InvalidOperationException("The actor has no character skeleton.");
        int race = Race(skeleton);
        var targets = Enumerable.Range(1, 18).Select(n => new IdleModTarget(n * 100 + 1,
            RaceNames[(n - 1) / 2] + (n % 2 == 1 ? " male" : " female"),
            Enumerable.Range(1, 6).Where(slot => data.FileExists(BodyPath(n * 100 + 1, slot, "start"))
                && data.FileExists(BodyPath(n * 100 + 1, slot, "loop"))).ToImmutableArray())).ToImmutableArray();
        return new IdleModChoices(actor.Name + " static pose", race, targets);
    });

    private static string BodyPath(int race, int slot, string clip) =>
        $"chara/human/c{race:0000}/animation/a0001/bt_common/emote/pose{slot:00}_{clip}.pap";
    private static unsafe int Race(ISkeleton skeleton)
    {
        var model = (CharacterBase*)skeleton.CharacterBaseAddress;
        if (model == null || model->GetModelType() != CharacterBase.ModelType.Human || model->Skeleton == null)
            throw new InvalidOperationException("Idle export currently supports humanoid actors only.");
        return ((Human*)model)->RaceSexId;
    }

    private sealed record Captured(int Race, string Face, IdleHavokEncoder.SkeletonLayout BodyLayout,
        IdleHavokEncoder.SkeletonLayout FaceLayout, IdleSkeletonPose Body, IdleSkeletonPose Expression);

    public async Task<IdleModPackage> CaptureAsync(ActorId actorId, IdleModOptions options)
    {
        var completion = new TaskCompletionSource<Captured>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    completion.TrySetResult(Capture(actorId, character, options.Slot));
                    return true;
                }
                catch (Exception ex) { error = ex; return false; }
            }, ok =>
            {
                if (!ok) completion.TrySetException(error ?? new InvalidOperationException("The pose did not finish refreshing."));
            });
            if (!started.Success) throw new InvalidOperationException(started.Detail);
        });
        var captured = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var files = new List<IdleModFile>();
        // Snapshot once; independent native resources are then serialized on the
        // framework in bounded race/face batches, without holding live bone wrappers.
        foreach (int race in options.Races)
        {
            var faces = race == captured.Race ? new[] { captured.Face } : Enumerable.Range(1, 8)
                .Select(i => $"f{i:0000}").Where(face => data.FileExists(FaceSkeletonPath(race, face))
                    && data.FileExists(FacePath(race, face, "nonresident/emot/joy"))
                    && data.FileExists(FacePath(race, face, "resident/face"))).ToArray();
            if (faces.Length == 0) throw new InvalidDataException($"No supported face skeletons for c{race:0000}.");
            foreach (string face in faces)
            {
                var batch = await framework.RunOnFrameworkThread(() => Build(captured, race, face, options.Slot));
                files.AddRange(files.Any(f => f.GamePath == batch[0].GamePath) ? batch.Skip(2) : batch);
                await Task.Delay(1);
            }
        }
        return new(options.Name, $"Standing /cpose {options.Slot} replacement with baked body and expression. " +
            "Other races use experimental bone-name retargeting; proportions and contact may differ. " +
            "The source race requires the same face, skeleton mods and Customize+ profile. " +
            "Entry is sine-eased; exit uses the game's normal blend-out.", files);
    }

    private Captured Capture(ActorId id, ISkeleton skeleton, int slot)
    {
        int race = Race(skeleton);
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
        // The source may be excluded from the targets and have fewer cpose slots.
        // Its entry sample only seeds capture; each destination uses its own slot.
        int sourceSlot = data.FileExists(BodyPath(race, slot, "start")) ? slot : 1;
        var start = new PapAnimationDocument(Read(BodyPath(race, sourceSlot, "start")));
        var neutral = new PapAnimationDocument(Read($"chara/human/{raceName}/animation/{face}/resident/face.pap"));
        int neutralIndex = neutral.Clips.Single(c => c.Name == "cfxf_base").BindingIndex;
        var bodyIdle = _encoder.SampleStart(start.HavokBytes, bodySkeleton, start.BodyClip.BindingIndex);
        var faceIdle = _encoder.SampleStart(neutral.HavokBytes, faceSkeleton, neutralIndex);
        var facePartial = skeleton.Bones.Where(b => b.BoneName.StartsWith("j_f_", StringComparison.Ordinal))
            .Select(b => b.PartialId).Distinct().ToArray();
        if (facePartial.Length != 1) throw new InvalidOperationException("The actor does not have one identifiable face skeleton.");
        var bodyPose = Tracks(bodyLayout, RemapIdle(vanillaBodyLayout, bodyIdle, bodyLayout), 0);
        var expression = Tracks(faceLayout, RemapIdle(vanillaFaceLayout, faceIdle, faceLayout), facePartial[0]);
        return new(race, face, bodyLayout, faceLayout, bodyPose, expression);

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

    private static string FaceSkeletonPath(int race, string face) => $"chara/human/c{race:0000}/skeleton/face/{face}/skl_c{race:0000}{face}.sklb";
    private static string FacePath(int race, string face, string clip) => $"chara/human/c{race:0000}/animation/{face}/{clip}.pap";
    private byte[] Read(string path) => data.GetFile(path)?.Data ?? throw new FileNotFoundException("Unsupported idle resource: " + path);

    private IReadOnlyList<IdleModFile> Build(Captured source, int race, string face, int slot)
    {
        var start = new PapAnimationDocument(Read(BodyPath(race, slot, "start")));
        var loop = new PapAnimationDocument(Read(BodyPath(race, slot, "loop")));
        var template = new PapAnimationDocument(Read(FacePath(race, face, "nonresident/emot/joy")));
        var body = source.Body;
        var expression = source.Expression;
        if (race != source.Race)
        {
            var bodyBytes = Read($"chara/human/c{race:0000}/skeleton/base/b0001/skl_c{race:0000}b0001.sklb");
            var faceBytes = Read(FaceSkeletonPath(race, face));
            var neutral = new PapAnimationDocument(Read(FacePath(race, face, "resident/face")));
            body = IdlePoseRetargeter.Retarget(source.BodyLayout, source.Body, _encoder.ReadSkeleton(bodyBytes),
                _encoder.SampleStart(start.HavokBytes, bodyBytes, start.BodyClip.BindingIndex));
            expression = IdlePoseRetargeter.Retarget(source.FaceLayout, source.Expression, _encoder.ReadSkeleton(faceBytes),
                _encoder.SampleStart(neutral.HavokBytes, faceBytes, neutral.Clips.Single(c => c.Name == "cfxf_base").BindingIndex));
            // The face root is an attachment, never a second exported head rotation.
            expression = expression with { Tracks = expression.Tracks.Select(t => t.BoneIndex == 0 ? t with { Posed = t.Idle } : t).ToImmutableArray() };
        }
        int entryFrames = start.DurationFrames, holdFrames = loop.DurationFrames;
        var samples = IdleAnimationSamples.Create(body, expression, entryFrames / 30f);
        return IdlePapBuilder.Files($"c{race:0000}", face, start, loop, template,
            _encoder.Encode(_encoder.Extract(start.HavokBytes, start.BodyClip.BindingIndex), 0, samples.Body, samples.Body.Entry),
            _encoder.Encode(_encoder.Extract(loop.HavokBytes, loop.BodyClip.BindingIndex), 0, samples.Body, samples.Body.Hold with { DurationSeconds = holdFrames / 30f }),
            _encoder.Encode(_encoder.Extract(template.HavokBytes, template.FaceClip.BindingIndex), 0, samples.Expression, samples.Expression.Entry),
            _encoder.Encode(_encoder.Extract(template.HavokBytes, template.FaceClip.BindingIndex), 0, samples.Expression, samples.Expression.Hold with { DurationSeconds = holdFrames / 30f }),
            slot, entryFrames, holdFrames);
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
