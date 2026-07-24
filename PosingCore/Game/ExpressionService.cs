using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Ktisis v0.4.0.0 action-unit expression blending. Per-race catalog deltas are
/// weighted and written to one named head-relative pose layer (the source's
/// verified convention — see docs/services/expression-service.md). The layer is
/// replaced on every slider change and never clears interactive face-bone
/// edits. Race/tribe resolve from customize bytes; a combination without a
/// catalog is quietly unavailable instead of destructively applying another
/// race's face data.
/// </summary>
public interface IExpressionService
{
    bool IsAvailable { get; }

    /// <summary>Action units for the actor's race catalog (Id, Label,
    /// Bidirectional); empty when the actor's customize combination has no
    /// catalog.</summary>
    IReadOnlyList<(string Id, string Label, bool Bidirectional)> GetUnits(IActor actor);

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
            _sessions.Clear();
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
    /// applied destructively.</summary>
    private unsafe string? CatalogKeyFor(IActor actor)
    {
        try
        {
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
            if (chara == null)
                return null;

            var customize = chara->DrawData.CustomizeData;
            byte race = customize.Race;
            byte sex = customize.Sex;      // 0 masculine, 1 feminine
            byte tribe = customize.Tribe;
            string gender = sex == 1 ? "Feminine" : "Masculine";
            string key = (race, tribe) switch
            {
                (1, 2) => $"Hyur_{gender}_Highlander",
                (1, _) => $"Hyur_{gender}_Midlander",
                (2, _) => $"Elezen_{gender}",
                (3, _) => $"Lalafell_{gender}",
                (4, _) => $"Miqote_{gender}",
                (5, 10) => $"Roegadyn_{gender}_Hellsguard",
                (5, _) => $"Roegadyn_{gender}_SeaWolf",
                (6, _) => $"AuRa_{gender}",
                (7, _) => $"Hrothgar_{gender}",
                (8, _) => $"Viera_{gender}",
                _ => "",
            };
            return _catalogs.ContainsKey(key) ? key : null;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<(string Id, string Label, bool Bidirectional)> GetUnits(IActor actor)
    {
        if (!IsAvailable) return Array.Empty<(string, string, bool)>();
        return CatalogKeyFor(actor) is { } key
            ? _catalogs[key].Select(u => (u.Id, u.Label, u.Bidirectional)).ToList()
            : Array.Empty<(string, string, bool)>();
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

        var poseInfo = _posing.GetPoseInfo(skeleton);
        foreach (var boneName in affected)
        {
            var bone = skeleton.GetBone(boneName);
            if (bone == null)
                continue;

            var info = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
            if (blended.TryGetValue(boneName, out var delta) && !IsIdentityDelta(delta))
                info.SetLayerTransform(ExpressionLayer, delta, TransformComponents.None, TransformFrame.HeadRelative);
            else
                info.RemoveLayer(ExpressionLayer);
        }
    }

    private void RemoveLayers(ISkeleton skeleton, IEnumerable<ActionUnit> units)
    {
        var poseInfo = _posing.GetPoseInfo(skeleton);
        foreach (var boneName in units.SelectMany(unit => unit.Bones.Keys).Distinct(StringComparer.Ordinal))
        {
            var bone = skeleton.GetBone(boneName);
            if (bone != null)
                poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).RemoveLayer(ExpressionLayer);
        }
    }

    private static bool IsIdentityDelta(Transform delta)
        => delta.Position.LengthSquared() < 1e-12f
           && delta.Scale.LengthSquared() < 1e-12f
           && MathF.Abs(Quaternion.Dot(delta.Rotation, Quaternion.Identity)) > 0.999999f;
}
