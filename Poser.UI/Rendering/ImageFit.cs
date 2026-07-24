namespace Poser.UI;

/// <summary>CSS-shaped object-fit values for raster images on element backgrounds.</summary>
public enum ImageFit
{
    /// <summary>Stretch to fill, ignoring aspect ratio.</summary>
    Fill,
    /// <summary>Scale up uniformly to cover the box; may crop.</summary>
    Cover,
    /// <summary>Scale down uniformly to fit inside the box; letterbox.</summary>
    Contain,
    /// <summary>Native pixel dimensions, top-left aligned.</summary>
    None,
}
