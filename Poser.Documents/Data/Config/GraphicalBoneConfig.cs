using System.Collections.Generic;
using System.Numerics;

namespace Poser.Data.Config;

/// <summary>
/// Configuration for graphical bone selection UI.
/// Contains image sections and bone positions for the body map.
/// </summary>
public class GraphicalBoneConfig
{
    /// <summary>
    /// Dictionary of pose image sections keyed by section name.
    /// </summary>
    public Dictionary<string, PoseImageSection> PoseImages { get; set; } = new();

    /// <summary>
    /// Processes parent references to merge bone lists.
    /// Call after loading JSON.
    /// </summary>
    public void ProcessParentReferences()
    {
        foreach (var section in PoseImages.Values)
        {
            if (string.IsNullOrEmpty(section.Parent))
                continue;

            if (PoseImages.TryGetValue(section.Parent, out var parent))
            {
                // Add parent bones to this section
                section.Bones.AddRange(parent.Bones);
            }
        }
    }
}

/// <summary>
/// A section of the pose image containing bone positions.
/// </summary>
public class PoseImageSection
{
    /// <summary>
    /// Name of the image file (without extension).
    /// </summary>
    public string? Image { get; set; }

    /// <summary>
    /// Optional parent section to inherit bones from.
    /// </summary>
    public string? Parent { get; set; }

    /// <summary>
    /// List of bones with their positions in this section.
    /// </summary>
    public List<GraphicalBoneEntry> Bones { get; set; } = new();
}

/// <summary>
/// A single bone entry with its position in the image.
/// </summary>
public class GraphicalBoneEntry
{
    /// <summary>
    /// Bone name (e.g., "j_kubi", "j_sebo_a").
    /// Special value "!model" indicates the model root.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Position in image coordinates as a string "x, y".
    /// Parsed into Vector2 after loading.
    /// </summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>
    /// Parsed position as Vector2.
    /// </summary>
    public Vector2 PositionVector { get; set; }
}
