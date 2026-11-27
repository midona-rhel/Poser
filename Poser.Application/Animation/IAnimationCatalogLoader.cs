namespace Poser.Application.Animation;

/// <summary>Builds the animation list from the game data once.</summary>
public interface IAnimationCatalogLoader
{
    /// <summary>Why the last build failed, shown in the picker; null otherwise.</summary>
    string? LastError { get; }
    /// <summary>Starts the build the first time; does nothing after that.</summary>
    void EnsureLoaded();
    /// <summary>Builds again after a failed build; called when the picker opens.</summary>
    void Retry();
}
