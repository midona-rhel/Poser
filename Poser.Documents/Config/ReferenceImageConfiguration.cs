using System;
using System.Collections.Generic;
using System.IO;

namespace Poser.Documents.Config;

/// <summary>
/// Persisted settings for one reference image. Position and size use unscaled
/// screen coordinates so changing the global UI scale does not move it.
///
/// <para>A zero <see cref="Width"/> means "never placed": the window seats
/// itself from the picture's own pixels the first frame the texture arrives,
/// and writes its placement back from then on.</para>
/// </summary>
public class ReferenceImageEntry
{
    /// <summary>A generated identity, independent of the file path, so the
    /// same image can be added more than once.</summary>
    public int Id { get; set; }

    public string FilePath { get; set; } = string.Empty;

    /// <summary>What the title bar says. Seeded from the file name; it is
    /// stored rather than recomputed so a renamed file keeps reading as the
    /// thing the user placed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Never below
    /// <see cref="ReferenceImageConfiguration.MinimumOpacity"/> — see there
    /// for why the floor exists.</summary>
    public float Opacity { get; set; } = 1f;

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; }

    public float Height { get; set; }

    /// <summary>Whether the image window is hidden. Hiding preserves the
    /// entry, placement, and opacity; closing removes the entry.</summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// Stores reference images across GPose sessions and plugin reloads. Each
/// entry has a generated identity and a minimum visible opacity.
/// </summary>
public class ReferenceImageConfiguration
{
    /// <summary>
    /// The floor a picture's opacity may not go under. A fully transparent
    /// reference window is indistinguishable from a closed one while still
    /// eating the pointer over its whole rect, so the control cannot reach
    /// invisibility. Brio floors its own slider at 10%
    /// (<c>ReferenceImageService.cs:170-174</c>); Ktisis has no floor at all
    /// and can be driven to zero (<c>RefOverlay.cs:73-76</c>).
    /// </summary>
    public const float MinimumOpacity = 0.25f;

    public List<ReferenceImageEntry> Images { get; set; } = new();

    /// <summary>The identity counter. Monotonic across a config's whole life:
    /// closing a picture never frees its id for reuse, so a stale window key
    /// can never bind to a different picture.</summary>
    public int NextId { get; set; } = 1;

    /// <summary>
    /// The next free id. Reads the roster as well as the counter so a config
    /// hand-edited (or written by a build whose counter lagged) still mints
    /// something no live entry holds.
    /// </summary>
    public int MintId()
    {
        int next = NextId;
        for (int i = 0; i < Images.Count; i++)
            if (Images[i].Id >= next)
                next = Images[i].Id + 1;
        NextId = next + 1;
        return next;
    }

    /// <summary>Adds a picture. The same path may be added any number of
    /// times and each add is its own entry with its own id.</summary>
    public ReferenceImageEntry Add(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var entry = new ReferenceImageEntry
        {
            Id = MintId(),
            FilePath = filePath,
            Name = NameFor(filePath),
            Opacity = 1f,
        };
        Images.Add(entry);
        return entry;
    }

    public bool Remove(int id)
    {
        for (int i = 0; i < Images.Count; i++)
            if (Images[i].Id == id)
            {
                Images.RemoveAt(i);
                return true;
            }
        return false;
    }

    /// <summary>Clamps opacity from every source, including edited or older
    /// configuration files.</summary>
    public static float ClampOpacity(float value) =>
        float.IsNaN(value)
            ? 1f
            : Math.Clamp(value, MinimumOpacity, 1f);

    /// <summary>The file's own name, extension dropped. A path that names no
    /// file at all still has to read as something.</summary>
    public static string NameFor(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "Reference";
        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(filePath);
        }
        catch (ArgumentException)
        {
            name = string.Empty;
        }
        return string.IsNullOrWhiteSpace(name) ? "Reference" : name;
    }
}
