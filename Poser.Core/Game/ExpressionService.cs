using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Ktisis v0.4.0.0 action-unit expression blending. Per-race catalog deltas are
/// weighted and written to one named head-relative pose layer (the source's
/// verified convention — see docs/features/expression-gaze-and-ik.md). The layer is
/// replaced on every slider change and never clears interactive face-bone
/// edits. Race/tribe resolve from customize bytes; a combination without a
/// catalog is quietly unavailable instead of destructively applying another
/// race's face data.
/// </summary>
public interface IExpressionService
{
    bool IsAvailable { get; }

    /// <summary>Action units for the actor's race catalog (Id, Label,
    /// Bidirectional, Available); empty when the actor's customize
    /// combination has no catalog. A unit with zero resolvable target bones
    /// on the actor's current skeleton reports Available = false — it is
    /// presented as unavailable, never as a functional slider that performs
    /// no work.</summary>
    IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(IActor actor);

    float GetWeight(IActor actor, string unitId);

    /// <summary>Sets a unit weight (0..1, or −1..1 when bidirectional) and re-blends.</summary>
    void SetWeight(IActor actor, string unitId, float weight);

    /// <summary>Clears all weights and restores the captured neutral pose.</summary>
    void ResetExpression(IActor actor);

    bool HasActiveExpression(IActor actor);
}

public class ExpressionService : IExpressionService
{
    private const string ExpressionLayer = "expression";

    private sealed class ActionUnit
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Bidirectional { get; set; }
        public bool UsePosition { get; set; }
        public Dictionary<string, TransformJson> Bones { get; set; } = new();
    }

    private sealed class TransformJson
    {
        public Vector3 Position { get; set; }
        public Vector4 Rotation { get; set; } = new(0, 0, 0, 1);
        public Vector3 Scale { get; set; } = Vector3.One;
    }

    private sealed class CatalogJson
    {
        public List<GroupJson> Groups { get; set; } = new();
    }

    private sealed class GroupJson
    {
        public string Name { get; set; } = "";
        public List<ActionUnit> Units { get; set; } = new();
    }

    private sealed class Session
    {
        public string CatalogKey = "";
        public readonly Dictionary<string, float> Weights = new();
    }

    private readonly IPluginLog _log;
    private readonly ISkeletonService _skeletons;
    private readonly IBonePosingService _posing;
    private readonly IEventBus _events;
    private readonly Dictionary<string, List<ActionUnit>> _catalogs = new();
    private readonly Dictionary<EntityId, Session> _sessions = new();

    public bool IsAvailable => _catalogs.Count > 0;

    public ExpressionService(IPluginLog log, ISkeletonService skeletons, IBonePosingService posing, IEventBus events)
    {
        _log = log;
        _skeletons = skeletons;
        _posing = posing;
        _events = events;
        // Pose stacks (and with them every expression layer) clear on GPose
        // exit; the weight sessions must not outlive their layers.
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        LoadCatalogs();
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
        {
            _sessions.Clear();
            // The probes hold a skeleton reference each; a session that has
            // ended must not be the reason one stays reachable.
            _probes.Clear();
        }
    }

    private void LoadCatalogs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("Poser.Data.Expressions.") || !name.EndsWith(".json"))
                continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                var catalog = JsonSerializer.Deserialize<CatalogJson>(stream, options);
                if (catalog == null) continue;
                var key = name["Poser.Data.Expressions.".Length..^".json".Length];
                _catalogs[key] = catalog.Groups.SelectMany(g => g.Units).ToList();
            }
            catch (Exception ex)
            {
                _log.Warning($"ExpressionService: failed to load {name}: {ex.Message}");
            }
        }
        _log.Info($"ExpressionService: {_catalogs.Count} race catalogs loaded (Ktisis v0.4.0.0 data)");
    }

    /// <summary>Race/tribe/gender → catalog key (Ktisis v0.4.0.0 resolution,
    /// including the Roegadyn Sea Wolf/Hellsguard tribe split). A combination
    /// without a catalog — or unreadable customize data — returns null: the UI
    /// shows a quiet unavailable state and no other race's face data is ever
    /// applied destructively.
    ///
    /// <para>Every arm is a LITERAL rather than an interpolation over a
    /// gender word. The rail asks this question on every frame an actor is
    /// selected, and an interpolated key minted a fresh string — and hashed
    /// it against the catalog table — sixty times a second to answer with the
    /// same twenty characters it answered with last frame.</para></summary>
    private unsafe string? CatalogKeyFor(IActor actor)
    {
        try
        {
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
            if (chara == null)
                return null;

            var customize = chara->DrawData.CustomizeData;
            byte race = customize.Race;
            bool feminine = customize.Sex == 1;   // 0 masculine, 1 feminine
            byte tribe = customize.Tribe;
            string key = (race, tribe) switch
            {
                (1, 2) => feminine
                    ? "Hyur_Feminine_Highlander" : "Hyur_Masculine_Highlander",
                (1, _) => feminine
                    ? "Hyur_Feminine_Midlander" : "Hyur_Masculine_Midlander",
                (2, _) => feminine ? "Elezen_Feminine" : "Elezen_Masculine",
                (3, _) => feminine ? "Lalafell_Feminine" : "Lalafell_Masculine",
                (4, _) => feminine ? "Miqote_Feminine" : "Miqote_Masculine",
                (5, 10) => feminine
                    ? "Roegadyn_Feminine_Hellsguard"
                    : "Roegadyn_Masculine_Hellsguard",
                (5, _) => feminine
                    ? "Roegadyn_Feminine_SeaWolf"
                    : "Roegadyn_Masculine_SeaWolf",
                (6, _) => feminine ? "AuRa_Feminine" : "AuRa_Masculine",
                (7, _) => feminine ? "Hrothgar_Feminine" : "Hrothgar_Masculine",
                (8, _) => feminine ? "Viera_Feminine" : "Viera_Masculine",
                _ => "",
            };
            return _catalogs.ContainsKey(key) ? key : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One actor's answered availability, with everything that can change it.
    /// The answer is a property of the CATALOG and the SKELETON — which units
    /// have a target bone to write — and neither moves between frames.
    /// </summary>
    private sealed class UnitProbe
    {
        public string CatalogKey = "";
        public ISkeleton? Skeleton;
        public nint CharacterBase;
        public int BoneCount;
        public (string Id, string Label, bool Bidirectional, bool Available)[] Units =
            Array.Empty<(string, string, bool, bool)>();

        /// <summary>Whether the recorded answer still describes this actor. A
        /// replaced slot skeleton is a new instance AND a new character base;
        /// a partial arriving late changes the bone count without either.
        /// </summary>
        public bool Describes(string catalogKey, ISkeleton? skeleton) =>
            string.Equals(CatalogKey, catalogKey, StringComparison.Ordinal)
            && ReferenceEquals(Skeleton, skeleton)
            && (skeleton == null
                || (CharacterBase == skeleton.CharacterBaseAddress
                    && BoneCount == skeleton.Bones.Count));
    }

    private readonly Dictionary<EntityId, UnitProbe> _probes = new();

    /// <summary>
    /// The catalog's units with their availability. Answered from a per-actor
    /// probe rather than recomputed: the rail's EXPRESSION section calls this
    /// every frame an actor is selected, and the uncached answer built a
    /// whole-skeleton name lookup — one dictionary and one list per bone —
    /// plus a resolve list per catalog bone, per frame, to conclude what it
    /// concluded the frame before.
    /// </summary>
    public IReadOnlyList<(string Id, string Label, bool Bidirectional, bool Available)> GetUnits(IActor actor)
    {
        if (!IsAvailable || CatalogKeyFor(actor) is not { } key)
            return Array.Empty<(string, string, bool, bool)>();

        var skeleton = _skeletons.GetSkeleton(actor);
        if (skeleton is not { IsValid: true })
            skeleton = null;

        if (_probes.TryGetValue(actor.Id, out var probe)
            && probe.Describes(key, skeleton))
            return probe.Units;

        var units = _catalogs[key];
        (string, string, bool, bool)[] answered;
        if (skeleton == null)
        {
            answered = new (string, string, bool, bool)[units.Count];
            for (int i = 0; i < units.Count; i++)
                answered[i] = (
                    units[i].Id, units[i].Label, units[i].Bidirectional, true);
        }
        else
        {
            var byName = BuildBoneLookup(skeleton);
            answered = new (string, string, bool, bool)[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                bool available = false;
                foreach (var name in unit.Bones.Keys)
                {
                    if (ResolveExpressionBones(byName, name).Count == 0)
                        continue;
                    available = true;
                    break;
                }
                answered[i] =
                    (unit.Id, unit.Label, unit.Bidirectional, available);
            }
        }

        _probes[actor.Id] = new UnitProbe
        {
            CatalogKey = key,
            Skeleton = skeleton,
            CharacterBase = skeleton?.CharacterBaseAddress ?? 0,
            BoneCount = skeleton?.Bones.Count ?? 0,
            Units = answered,
        };
        return answered;
    }

    /// <summary>All bone instances per canonical name, by complete identity.</summary>
    private static Dictionary<string, List<IBone>> BuildBoneLookup(ISkeleton skeleton)
    {
        var byName = new Dictionary<string, List<IBone>>(StringComparer.Ordinal);
        foreach (var bone in skeleton.Bones)
        {
            if (!byName.TryGetValue(bone.BoneName, out var list))
                byName[bone.BoneName] = list = new List<IBone>();
            list.Add(bone);
        }
        return byName;
    }

    /// <summary>
    /// Resolves the instances an expression delta targets. The game evaluates
    /// face data on the face/accessory partials (partial id ≥ 1);
    /// <c>ISkeleton.GetBone(name)</c> is first-writer-wins across ascending
    /// partials and can silently bind a partial-0 duplicate that the
    /// evaluated face partial never reads — the "Jaw Open does nothing"
    /// defect. Every evaluated-partial instance participates with its
    /// complete bone identity; partial 0 is used only when no higher-partial
    /// instance exists.
    /// </summary>
    private static List<IBone> ResolveExpressionBones(
        Dictionary<string, List<IBone>> byName, string boneName)
    {
        if (!byName.TryGetValue(boneName, out var instances))
            return new List<IBone>();
        var evaluated = instances.FindAll(bone => bone.PartialId > 0);
        return evaluated.Count > 0 ? evaluated : instances;
    }

    public float GetWeight(IActor actor, string unitId)
        => _sessions.TryGetValue(actor.Id, out var s) && s.Weights.TryGetValue(unitId, out var w) ? w : 0f;

    public bool HasActiveExpression(IActor actor)
        => _sessions.TryGetValue(actor.Id, out var s) && s.Weights.Values.Any(w => MathF.Abs(w) > 0.001f);

    public void SetWeight(IActor actor, string unitId, float weight)
    {
        var skeleton = _skeletons.GetSkeleton(actor);
        if (skeleton is not { IsValid: true })
            return;

        if (CatalogKeyFor(actor) is not { } catalogKey)
            return; // unsupported customize combination — quiet unavailable
        if (_sessions.TryGetValue(actor.Id, out var existing) && existing.CatalogKey != catalogKey)
        {
            RemoveLayers(skeleton, _catalogs[existing.CatalogKey]);
            _sessions.Remove(actor.Id);
        }

        if (!_sessions.TryGetValue(actor.Id, out var session))
            _sessions[actor.Id] = session = new Session { CatalogKey = catalogKey };

        var units = _catalogs[session.CatalogKey];
        var unit = units.FirstOrDefault(candidate => candidate.Id == unitId);
        if (unit == null)
            return;
        // An unavailable unit (zero resolvable target bones) stores no weight:
        // the UI presents it as unavailable and it must never look functional.
        var lookup = BuildBoneLookup(skeleton);
        if (unit.Bones.Keys.All(name => ResolveExpressionBones(lookup, name).Count == 0))
            return;

        var clamped = Math.Clamp(weight, unit.Bidirectional ? -1f : 0f, 1f);
        if (MathF.Abs(clamped) < 0.0001f)
            session.Weights.Remove(unitId);
        else
            session.Weights[unitId] = clamped;

        Blend(skeleton, session, units);
        if (session.Weights.Count == 0)
            _sessions.Remove(actor.Id);
    }

    public void ResetExpression(IActor actor)
    {
        if (!_sessions.Remove(actor.Id, out var session))
            return;

        var skeleton = _skeletons.GetSkeleton(actor);
        if (skeleton is { IsValid: true })
            RemoveLayers(skeleton, _catalogs[session.CatalogKey]);
    }

    /// <summary>
    /// Recomputes one head-relative expression layer per affected bone. No
    /// cached parent transforms or absolute targets are involved, so slider
    /// updates are idempotent and cannot amplify stale cross-partial
    /// coordinates. Units aggregate in catalog order; the source convention is
    /// a pre-multiply, so a later unit's rotation left-multiplies the
    /// accumulated head-frame rotation and weighted positions sum — the result
    /// is deterministic for any slider edit order.
    /// </summary>
    private void Blend(ISkeleton skeleton, Session session, List<ActionUnit> units)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var blended = new Dictionary<string, Transform>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            foreach (var boneName in unit.Bones.Keys)
                affected.Add(boneName);

            if (!session.Weights.TryGetValue(unit.Id, out var weight))
                continue;

            foreach (var (boneName, json) in unit.Bones)
            {
                var source = new Transform
                {
                    Position = json.Position,
                    Rotation = new Quaternion(json.Rotation.X, json.Rotation.Y, json.Rotation.Z, json.Rotation.W),
                    Scale = json.Scale
                };
                var weighted = PoseMath.WeightPoseDelta(source, weight, unit.UsePosition);
                blended[boneName] = blended.TryGetValue(boneName, out var current)
                    ? BonePoseInfo.Combine(weighted, current)
                    : weighted;
            }
        }

        var byName = BuildBoneLookup(skeleton);
        DiagnoseResolutionOnce(session.CatalogKey, units, byName);
        var poseInfo = _posing.GetPoseInfo(skeleton);
        foreach (var boneName in affected)
        {
            foreach (var bone in ResolveExpressionBones(byName, boneName))
            {
                var info = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
                if (blended.TryGetValue(boneName, out var delta) && !IsIdentityDelta(delta))
                    info.SetLayerTransform(ExpressionLayer, delta, TransformComponents.None, TransformFrame.HeadRelative);
                else
                    info.RemoveLayer(ExpressionLayer);
            }
        }
    }

    private void RemoveLayers(ISkeleton skeleton, IEnumerable<ActionUnit> units)
    {
        var byName = BuildBoneLookup(skeleton);
        var poseInfo = _posing.GetPoseInfo(skeleton);
        foreach (var boneName in units.SelectMany(unit => unit.Bones.Keys).Distinct(StringComparer.Ordinal))
            foreach (var bone in ResolveExpressionBones(byName, boneName))
                poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).RemoveLayer(ExpressionLayer);
    }

    private readonly HashSet<string> _diagnosedCatalogs = new(StringComparer.Ordinal);

    /// <summary>
    /// One diagnostic record per catalog per session: which instances each
    /// catalog bone resolves to (with partial ids) and which units have no
    /// resolvable target at all — the PBI-002 round-1 Jaw Open diagnosis.
    /// </summary>
    private void DiagnoseResolutionOnce(
        string catalogKey, List<ActionUnit> units, Dictionary<string, List<IBone>> byName)
    {
        if (!_diagnosedCatalogs.Add(catalogKey))
            return;
        foreach (var unit in units)
        {
            var parts = unit.Bones.Keys
                .Select(name =>
                {
                    var resolved = ResolveExpressionBones(byName, name);
                    return resolved.Count == 0
                        ? $"{name}: unresolved"
                        : $"{name}: [{string.Join(", ", resolved.Select(b => $"p{b.PartialId}"))}]";
                });
            var line = string.Join("  ", parts);
            if (unit.Bones.Keys.All(name => ResolveExpressionBones(byName, name).Count == 0))
                _log.Warning($"ExpressionService: unit {unit.Id} ({catalogKey}) has no resolvable bones — {line}");
            else
                _log.Debug($"ExpressionService: {unit.Id} ({catalogKey}) resolves {line}");
        }
    }

    private static bool IsIdentityDelta(Transform delta)
        => delta.Position.LengthSquared() < 1e-12f
           && delta.Scale.LengthSquared() < 1e-12f
           && MathF.Abs(Quaternion.Dot(delta.Rotation, Quaternion.Identity)) > 0.999999f;
}
