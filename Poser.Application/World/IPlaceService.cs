namespace Poser.Application.World;

/// <summary>
/// Territory ID and display name saved with a document. Zero and null mean the
/// location was not recorded, allowing file listings to group by date alone.
/// </summary>
public readonly record struct CapturePlace(uint TerritoryId, string? PlaceName);

/// <summary>
/// Resolves the current territory and place name for scene capture and pose autosave.
/// </summary>
public interface IPlaceService
{
    /// <summary>Framework thread: this reads live client state.</summary>
    CapturePlace Current { get; }
}
