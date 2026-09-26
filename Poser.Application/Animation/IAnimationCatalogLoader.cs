namespace Poser.Application.Animation;

/// <summary>Loads the shared animation catalog once.</summary>
public interface IAnimationCatalogLoader
{
    void EnsureLoaded();
}
