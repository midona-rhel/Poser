using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>Transient feedback for the width slider; never saved or used by the solver.</summary>
internal static class IkWidthPreview
{
    public static BoneId? Target;
    public static float Radius;
}
