namespace Poser.Game.Journal;

/// <summary>The value sessions the shell reaches per entity kind, so a verb
/// that touches several kinds (show, hide, night, pause) takes one
/// dependency.</summary>
public sealed record EntitySessions(
    Poser.Application.Presentation.IActorValueControl Actors,
    LightSession Lights,
    CameraSession Cameras,
    PropSession Props,
    WorldObjectSession WorldObjects,
    OverlaySession Overlays);
