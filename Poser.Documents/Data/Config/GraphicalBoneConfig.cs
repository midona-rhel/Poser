using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Poser.Data.Config;

/// <summary>
/// Configuration for graphical bone selection UI.
/// Contains image sections and bone positions for the body map.
/// Read-only once loaded: one instance is shared by every bone-map pane.
/// </summary>
public sealed class GraphicalBoneConfig
{
    /// <summary>
    /// Dictionary of pose image sections keyed by section name.
    /// </summary>
    public IReadOnlyDictionary<string, PoseImageSection> PoseImages { get; init; } =
        new Dictionary<string, PoseImageSection>();

    /// <summary>
    /// Merges each section's parent bones into its own list. Called once by
    /// the reader after loading JSON.
    /// </summary>
    internal void ProcessParentReferences()
    {
        foreach (var section in PoseImages.Values)
        {
            if (string.IsNullOrEmpty(section.Parent))
                continue;

            if (PoseImages.TryGetValue(section.Parent, out var parent))
                section.Bones = section.Bones.Concat(parent.Bones).ToList();
        }
    }
}

/// <summary>
/// A section of the pose image containing bone positions.
/// </summary>
public sealed class PoseImageSection
{
    /// <summary>
    /// Name of the image file (without extension).
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// Optional parent section to inherit bones from.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>
    /// List of bones with their positions in this section.
    /// </summary>
    [JsonInclude]
    public IReadOnlyList<GraphicalBoneEntry> Bones { get; internal set; } = [];
}

/// <summary>
/// A single bone entry with its position in the image.
/// </summary>
public sealed class GraphicalBoneEntry
{
    /// <summary>
    /// Bone name (e.g., "j_kubi", "j_sebo_a").
    /// Special value "!model" indicates the model root.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Position in image coordinates as a string "x, y".
    /// Parsed into Vector2 after loading.
    /// </summary>
    public string Position { get; init; } = string.Empty;

    /// <summary>
    /// Parsed position as Vector2.
    /// </summary>
    [JsonIgnore]
    public Vector2 PositionVector { get; internal set; }
}
