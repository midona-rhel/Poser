namespace Poser.Services;

/// <summary>
/// Where the session is, in the two forms a document keeps: the durable
/// machine fact and the name resolved beside it. A zero id with a null name is
/// "not recorded" — nothing ever invents a placeholder, because ABSENCE is
/// what tells a listing to group a file by its day alone.
/// </summary>
public readonly record struct CapturePlace(uint TerritoryId, string? PlaceName);

/// <summary>
/// The one <c>TerritoryType</c> → <c>PlaceName</c> resolution. Whole-scene
/// capture and pose auto-save both stamp their documents from here, so "where
/// this was taken" means the same thing in a <c>.poserscene</c> and in a
/// <c>.pose</c>, and the Brio-verified lookup has a single home.
/// </summary>
public interface IPlaceService
{
    /// <summary>Framework thread: this reads live client state.</summary>
    CapturePlace Current { get; }
}
