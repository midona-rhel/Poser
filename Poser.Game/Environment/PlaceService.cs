using Dalamud.Plugin.Services;
using Lumina.Excel;
using Poser.Services;
using TerritoryRow = Lumina.Excel.Sheets.TerritoryType;

namespace Poser.Game.Environment;

/// <summary>
/// Resolves the current territory to a place name, following Brio's own
/// (CatalogWindow.cs:545): the <c>TerritoryType</c> row's <c>PlaceName</c>
/// link, read through <c>ValueNullable</c> because a territory row can carry
/// an unpopulated link, and <c>ExtractText</c> because the sheet string is
/// payload-encoded. A territory that resolves to nothing yields NO name rather
/// than a placeholder.
///
/// <para><c>IClientState.TerritoryType</c> is <c>uint</c> on this Dalamud, not
/// the <c>ushort</c> the neighbouring festival lookup uses, so nothing is cast
/// away here.</para>
/// </summary>
public sealed class PlaceService : IPlaceService
{
    private readonly IClientState _clientState;

    /// <summary>The territory sheet, resolved once. Excel rows are immutable
    /// data, so holding the sheet costs nothing per read.</summary>
    private readonly ExcelSheet<TerritoryRow>? _territories;

    public PlaceService(IClientState clientState, IDataManager data)
    {
        _clientState = clientState;
        _territories = data.GetExcelSheet<TerritoryRow>();
    }

    public CapturePlace Current
    {
        get
        {
            uint territory = _clientState.TerritoryType;
            if (territory == 0 || _territories is null)
                return new CapturePlace(territory, null);
            if (_territories.GetRowOrDefault(territory) is not { } row)
                return new CapturePlace(territory, null);
            var name = row.PlaceName.ValueNullable?.Name.ExtractText();
            return new CapturePlace(
                territory,
                string.IsNullOrWhiteSpace(name) ? null : name);
        }
    }
}
