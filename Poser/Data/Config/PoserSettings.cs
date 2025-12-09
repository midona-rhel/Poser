using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Core;
using Poser.Entities;

namespace Poser.Data.Config;

/// <summary>
/// Global settings for Poser.
/// Simple singleton for now - can be expanded to save/load from disk later.
/// </summary>
public class PoserSettings
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static PoserSettings Instance { get; } = new();

    /// <summary>
    /// Whether to show NSFW bone categories (genitals, etc.)
    /// </summary>
    public bool ShowNsfwBones { get; set; } = false;

    /// <summary>
    /// Whether to replace actor names with random 5-character codes for privacy.
    /// </summary>
    public bool AnonymousMode { get; set; } = false;

    private readonly Dictionary<EntityId, string> _anonymousNames = new();
    private static readonly Random _random = new();

    private PoserSettings() { }

    /// <summary>
    /// Gets the display name for an entity. Returns anonymous name if AnonymousMode is enabled.
    /// </summary>
    public string GetDisplayName(IEntity entity)
    {
        if (!AnonymousMode)
            return entity.Name;

        if (!_anonymousNames.TryGetValue(entity.Id, out var anonName))
        {
            anonName = GenerateRandomName();
            _anonymousNames[entity.Id] = anonName;
        }
        return anonName;
    }

    private static string GenerateRandomName()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 5).Select(_ => chars[_random.Next(chars.Length)]).ToArray());
    }
}
