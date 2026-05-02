using System;
using System.IO;
using System.Linq;
using System.Xml;

using Poser.Data.Config;

namespace Poser.Data;

/// <summary>
/// Reads bone category configuration from XML.
/// </summary>
public static class CategoryReader
{
    private const string CategoryTag = "Category";
    private const string BonesTag = "Bones";

    /// <summary>
    /// Reads category configuration from an XML stream.
    /// </summary>
    public static CategoryConfig ReadStream(Stream stream)
    {
        var config = new CategoryConfig();

        using var reader = XmlReader.Create(stream);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == CategoryTag)
            {
                ReadCategory(reader, config, parentId: null);
            }
        }

        config.ResolveHierarchy();
        return config;
    }

    /// <summary>
    /// Reads category configuration from an XML string.
    /// </summary>
    public static CategoryConfig ReadString(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return ReadStream(stream);
    }

    /// <summary>
    /// Reads category configuration from embedded resource.
    /// </summary>
    public static CategoryConfig ReadEmbeddedResource()
    {
        var assembly = typeof(CategoryReader).Assembly;
        var resourceName = "Poser.Data.Schema.Categories.xml";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Could not find embedded resource: {resourceName}");
        }

        return ReadStream(stream);
    }

    private static BoneCategory ReadCategory(XmlReader reader, CategoryConfig config, string? parentId)
    {
        var id = reader.GetAttribute("Id") ?? "Unknown";
        var category = new BoneCategory(id)
        {
            IsNsfw = reader.GetAttribute("IsNsfw") == "true",
            IsDefault = reader.GetAttribute("IsDefault") == "true",
            ParentCategoryId = parentId
        };

        config.AddCategory(category);

        // Handle self-closing tags
        if (reader.IsEmptyElement)
            return category;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.Name == CategoryTag:
                    // Nested category - recurse with this category as parent
                    ReadCategory(reader, config, category.Id);
                    break;

                case XmlNodeType.Element when reader.Name == BonesTag:
                    ReadBones(reader, category);
                    break;

                case XmlNodeType.EndElement when reader.Name == CategoryTag:
                    return category;
            }
        }

        return category;
    }

    private static void ReadBones(XmlReader reader, BoneCategory category)
    {
        reader.Read();
        if (reader.NodeType != XmlNodeType.Text)
            return;

        var innerText = reader.Value;
        var bones = innerText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(ln => ln.Trim())
            .Where(ln => !string.IsNullOrEmpty(ln));

        category.Bones.AddRange(bones);
    }
}
