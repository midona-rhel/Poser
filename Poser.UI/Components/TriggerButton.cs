namespace Poser.UI;

/// <summary>
/// A button that OWNS a floating surface. Not a kind of button — a button
/// plus its delta: the surface is the trigger's own child (so the popup
/// handle and the anchor rect read off the button's path), it opens on the
/// press edge (the surface claims the exclusive chain before anything under
/// it answers), and the box may not clip (the surface is a child and would
/// be cut to the button's bounds). Everything button-ish is the Button's.
/// </summary>
internal readonly record struct TriggerButton
{
    public required Button Button { get; init; }

    public required UiNode Surface { get; init; }

    public static implicit operator UiNode(TriggerButton trigger)
    {
        UiNode node = trigger.Button.Compose() with
        {
            ClipChildren = false,
            ActivateOn = Activation.Press,
            Children = trigger.Surface,
            OpensPortalNode = trigger.Surface.Index,
        };
        Crystarium.AnchorPortal(trigger.Surface, node);
        return node;
    }
}
