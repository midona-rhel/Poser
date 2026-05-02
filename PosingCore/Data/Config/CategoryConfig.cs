using System.Collections.Generic;
using System.Linq;

namespace Poser.Data.Config;

/// <summary>
/// Holds the complete bone category configuration loaded from Categories.xml.
/// </summary>
public class CategoryConfig
{
    /// <summary>
    /// All categories indexed by their ID.
    /// </summary>
    private readonly Dictionary<string, BoneCategory> _categoriesById = new();

    /// <summary>
    /// Root-level categories (no parent).
    /// </summary>
    public List<BoneCategory> RootCategories { get; } = new();

    /// <summary>
    /// The default category for bones not in any other category.
    /// </summary>
    public BoneCategory? DefaultCategory { get; private set; }

    /// <summary>
    /// Adds a category to the configuration.
    /// </summary>
    public void AddCategory(BoneCategory category)
    {
        _categoriesById[category.Id] = category;

        if (category.IsDefault)
            DefaultCategory = category;
    }

    /// <summary>
    /// Resolves parent-child relationships after all categories are loaded.
    /// </summary>
    public void ResolveHierarchy()
    {
        foreach (var category in _categoriesById.Values)
        {
            if (string.IsNullOrEmpty(category.ParentCategoryId))
            {
                RootCategories.Add(category);
            }
            else if (_categoriesById.TryGetValue(category.ParentCategoryId, out var parent))
            {
                parent.Children.Add(category);
            }
        }
    }

    /// <summary>
    /// Gets a category by ID.
    /// </summary>
    public BoneCategory? GetCategory(string id)
    {
        return _categoriesById.GetValueOrDefault(id);
    }

    /// <summary>
    /// Gets all categories.
    /// </summary>
    public IEnumerable<BoneCategory> GetAllCategories() => _categoriesById.Values;

    /// <summary>
    /// Finds the category that contains a specific bone.
    /// Returns the most specific (deepest) category.
    /// </summary>
    public BoneCategory? FindCategoryForBone(string boneName)
    {
        BoneCategory? found = null;
        int depth = -1;

        void Search(BoneCategory category, int currentDepth)
        {
            if (category.Bones.Contains(boneName))
            {
                if (currentDepth > depth)
                {
                    found = category;
                    depth = currentDepth;
                }
            }

            foreach (var child in category.Children)
            {
                Search(child, currentDepth + 1);
            }
        }

        foreach (var root in RootCategories)
        {
            Search(root, 0);
        }

        return found ?? DefaultCategory;
    }
}
