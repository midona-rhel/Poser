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

    private PoserSettings() { }
}
