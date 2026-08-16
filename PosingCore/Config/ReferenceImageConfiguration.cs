using System;
using System.Collections.Generic;
using System.IO;

namespace Poser.Config;

/// <summary>
/// One pinned reference picture, as the config stores it. Placement is
/// LOGICAL (unscaled) screen space, so a stored window survives a change of
/// global UI scale the way every other stated size in the codebase does.
///
/// <para>A zero <see cref="Width"/> means "never placed": the window seats
/// itself from the picture's own pixels the first frame the texture arrives,
/// and writes its placement back from then on.</para>
/// </summary>
public class ReferenceImageEntry
{
    /// <summary>Identity, MINTED — never derived from the path. Brio derives
    /// its entity id from <c>path.GetHashCode()</c>
    /// (<c>Brio/Entities/ReferenceImageEntity.cs:31-35</c>), so the same file
    /// added twice collides and the second add silently replaces the first.
    /// The same picture is a legitimate second reference (two crops of one
    /// sheet, two placements of one pose sheet), so identity is a counter.
    /// </summary>
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

    /// <summary>Taken off screen without being given up. This is the sidebar
    /// eye's state, and it is STORED for the same reason the placement is: a
    /// picture set aside stays set aside across a session, or the eye would
    /// silently undo itself the next time the roster restored.
    ///
    /// <para>Hidden is not closed. Closing removes the entry; hiding keeps the
    /// entry, its placement and its opacity, and only takes the window down —
    /// which is what makes the eye a toggle rather than a delete.</para>
    /// </summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// The reference-image roster and its two invariants: minted identity and the
/// opacity floor.
///
/// <para>PERSISTENCE SCOPE follows Ktisis, not Brio: the roster is CONFIG, so
/// pictures survive leaving GPose, re-entering it, and reloading the plugin
/// (<c>Ktisis/Data/Config/Sections/EditorConfig.cs:44</c>, rebuilt at
/// <c>Ktisis/Scene/SceneManager.cs:87-93</c>). Brio keeps its images in the
/// entity container and drops every one of them on GPose exit
/// (<c>Brio/Services/ReferenceImageService.cs:29-39</c>). Placement is stored
/// here too — Ktisis leaves it to ImGui's own ini and therefore loses it on a
/// window-id change — because a reference picture whose position resets is
/// worthless.</para>
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

    /// <summary>THE opacity gate. Every write goes through it — the slider's
    /// own range states the same floor, and this is what makes a config
    /// written by hand or by an older build obey it too.</summary>
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
