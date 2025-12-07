using System.Collections.Generic;

namespace Poser.Data.Config;

/// <summary>
/// Represents a bone category that can contain bones and subcategories.
/// Categories form a hierarchical tree structure.
/// </summary>
public class BoneCategory
{
    public string Id { get; }
    public string? ParentCategoryId { get; set; }
    public bool IsNsfw { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// Bone names that belong directly to this category.
    /// </summary>
    public List<string> Bones { get; } = new();

    /// <summary>
    /// Child categories nested under this category.
    /// </summary>
    public List<BoneCategory> Children { get; } = new();

    public BoneCategory(string id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets a display-friendly name for this category.
    /// Converts "LeftBrow" to "Left Brow", etc.
    /// </summary>
    public string DisplayName => FormatDisplayName(Id);

    private static string FormatDisplayName(string id)
    {
        // Insert spaces before capital letters (except first)
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (i > 0 && char.IsUpper(c))
            {
                result.Append(' ');
            }
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// Gets all bones in this category and all descendant categories.
    /// </summary>
    public IEnumerable<string> GetAllBones()
    {
        foreach (var bone in Bones)
            yield return bone;

        foreach (var child in Children)
        {
            foreach (var bone in child.GetAllBones())
                yield return bone;
        }
    }

    /// <summary>
    /// Checks if this category or any descendant contains the given bone.
    /// </summary>
    public bool ContainsBone(string boneName)
    {
        if (Bones.Contains(boneName))
            return true;

        foreach (var child in Children)
        {
            if (child.ContainsBone(boneName))
                return true;
        }

        return false;
    }
}
