using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

/// <summary>Detached edit transaction. The caller owns persistence after a successful commit.</summary>
public sealed class BoneMapPresetDraft
{
    private readonly List<BoneMapPoint> _initial;
    private readonly List<BoneMapPoint> _defaults;
    private readonly Guid? _sourceId;
    private readonly BoneMapPreset? _sourceSnapshot;
    public BoneMapKind Kind { get; }
    public string Name { get; set; }
    private readonly List<BoneMapPoint> _points;
    public IReadOnlyList<BoneMapPoint> Points => _points;
    public Guid? SourceId => _sourceId;
    public bool HasChanges => Name != (_sourceSnapshot?.Name ?? string.Empty) || !_points.SequenceEqual(_initial);

    public BoneMapPresetDraft(BoneMapKind kind, IEnumerable<BoneMapPoint> defaults,
        BoneMapPreset? preset = null, IEnumerable<BoneMapPoint>? initial = null)
    {
        if (preset != null && preset.Kind != kind)
            throw new ArgumentException("The preset belongs to another map.", nameof(preset));
        Kind = kind;
        Name = preset?.Name ?? string.Empty;
        _sourceId = preset?.Id;
        _defaults = defaults.ToList();
        _initial = (initial ?? preset?.Points ?? _defaults).ToList();
        _points = _initial.ToList();
        _sourceSnapshot = preset == null ? null : Copy(preset);
    }

    public bool Contains(PortableBoneId bone) => _points.Any(point => point.Bone == bone);

    public void Add(PortableBoneId bone)
    {
        if (!bone.IsValid || Contains(bone)) return;
        var defaults = _defaults.Where(point => point.Bone == bone).ToArray();
        if (defaults.Length > 0) _points.AddRange(defaults);
        else _points.Add(new(bone, Kind == BoneMapKind.Body ? "body" : "face", 0.5f, 0.5f));
    }

    public void Remove(PortableBoneId bone) => _points.RemoveAll(point => point.Bone == bone);

    public void Move(PortableBoneId bone, string section, float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return;
        int at = _points.FindIndex(point => point.Bone == bone && point.Section == section);
        if (at >= 0) _points[at] = _points[at] with
            { X = Math.Clamp(x, 0f, 1f), Y = Math.Clamp(y, 0f, 1f) };
    }

    public bool CanReset(PortableBoneId bone, string section, bool toDefault) =>
        (toDefault ? _defaults : _initial).Any(point => point.Bone == bone && point.Section == section);

    public void Reset(PortableBoneId bone, string section, bool toDefault)
    {
        var source = (toDefault ? _defaults : _initial)
            .FirstOrDefault(point => point.Bone == bone && point.Section == section);
        int at = _points.FindIndex(point => point.Bone == bone && point.Section == section);
        if (source != null && at >= 0) _points[at] = source;
    }

    public string? Save(IList<BoneMapPreset> store, out Guid savedId)
    {
        savedId = Guid.Empty;
        string name = Name.Trim();
        if (name.Length == 0) return "Name the preset first.";
        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
            return "Default is reserved for the built-in map.";
        if (_points.Any(point => !point.Bone.IsValid || string.IsNullOrWhiteSpace(point.Section)
            || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || point.X is < 0f or > 1f || point.Y is < 0f or > 1f)
            || _points.Select(point => (point.Bone, point.Section)).Distinct().Count() != _points.Count)
            return "The layout contains invalid or duplicate points.";
        var existing = _sourceId is { } id ? store.FirstOrDefault(item => item.Id == id) : null;
        // Two non-modal editors must not silently overwrite each other's edits.
        if (_sourceSnapshot is { } snapshot && (existing == null || existing.Kind != snapshot.Kind
            || existing.Name != snapshot.Name || !existing.Points.SequenceEqual(snapshot.Points)))
            return "This preset changed in another window. Reopen it before saving.";
        if (store.Any(item => item.Kind == Kind && item.Id != _sourceId
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "A preset with this name already exists for this map.";
        var saved = new BoneMapPreset
        {
            Id = _sourceId ?? Guid.NewGuid(), Name = name, Kind = Kind, Points = _points.ToList(),
        };
        if (existing == null) store.Add(saved);
        else store[store.IndexOf(existing)] = saved;
        savedId = saved.Id;
        return null;
    }

    public static BoneMapPreset Copy(BoneMapPreset preset) => new()
        { Id = preset.Id, Name = preset.Name, Kind = preset.Kind, Points = preset.Points.ToList() };

    public string? Delete(IList<BoneMapPreset> store)
    {
        if (_sourceSnapshot is not { } snapshot)
            return "Only saved custom presets can be deleted.";
        var existing = store.FirstOrDefault(item => item.Id == snapshot.Id);
        if (existing == null || existing.Kind != snapshot.Kind || existing.Name != snapshot.Name
            || !existing.Points.SequenceEqual(snapshot.Points))
            return "This preset changed in another window. Reopen it before deleting.";
        store.Remove(existing);
        return null;
    }

    public static IReadOnlyDictionary<PortableBoneId, BoneDescriptor> Available(ActorDescriptor actor)
    {
        // Duplicate portable keys are ambiguous, never a license to pick the first native index.
        return actor.Skeletons.SelectMany(skeleton => skeleton.Bones)
            .GroupBy(bone => PortableBoneId.From(bone.Id))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
    }
}
