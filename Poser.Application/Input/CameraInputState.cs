namespace Poser.Application.Input;

// UI supplies input ownership, Game supplies movement activity. No key APIs,
// native cameras or UI widgets cross this boundary.
public sealed class CameraInputState
{
    public bool PointerDragHeld { get; set; }
    public bool TextInputActive { get; set; }
    public bool FlightActive { get; set; }
}
