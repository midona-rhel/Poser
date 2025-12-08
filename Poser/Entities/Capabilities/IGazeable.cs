namespace Poser.Entities.Capabilities;

/// <summary>
/// Capability marker interface for entities that have gaze control (eyes, head, body tracking).
/// Implemented by: IActor
///
/// Note: Gaze control is done through IGazeService.
/// This interface marks entities that support gaze operations.
///
/// Gaze control and bone posing are mutually exclusive for gaze-related bones.
/// - When manipulating gaze bones directly → Gaze should be LOCKED
/// - When using gaze control UI → Gaze should be UNLOCKED to allow game animation
/// </summary>
public interface IGazeable
{
    // Marker interface - all gaze operations are through IGazeService
}
