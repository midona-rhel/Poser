namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Opens the popover, dropdown, colour picker or modal registered
    /// under <paramref name="id"/>. This is the ONE open path: it claims
    /// the exclusive chain before ImGui's popup stack, so the surface
    /// owns input — and occludes what is under it — from the frame it
    /// opens. Calling <c>ImGui.OpenPopup</c> directly skips that claim
    /// and leaves the surface unable to occlude anything.
    /// </summary>
    public static void OpenPopover(string id) => FloatingSurface.OpenPopup(id);
}
