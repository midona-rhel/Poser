using System.Collections.Generic;

namespace Poser.Services;

/// <summary>
/// Animation entry with ID and display name.
/// </summary>
public record AnimationEntry(ushort TimelineId, string Name, string Key, AnimationCategory Category, uint Icon);

/// <summary>
/// Category of animation for filtering.
/// </summary>
public enum AnimationCategory
{
    Emote,
    Action,
    Raw
}

/// <summary>
/// Provides access to game animation data.
/// </summary>
public interface IAnimationDataService
{
    /// <summary>
    /// Gets all known animations.
    /// </summary>
    IReadOnlyList<AnimationEntry> Animations { get; }

    /// <summary>
    /// Search animations by name or ID.
    /// </summary>
    IEnumerable<AnimationEntry> Search(string query, int maxResults = 50);

    /// <summary>
    /// Get animation entry by timeline ID.
    /// </summary>
    AnimationEntry? GetById(ushort timelineId);
}
