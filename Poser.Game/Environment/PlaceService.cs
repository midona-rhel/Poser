using Dalamud.Plugin.Services;
using Lumina.Excel;
using Poser.Application.World;
using TerritoryRow = Lumina.Excel.Sheets.TerritoryType;

namespace Poser.Game.Environment;

/// <summary>
/// Resolves the current territory's optional <c>PlaceName</c>. Missing rows or
/// links produce no name rather than a placeholder. The full unsigned territory
/// id is preserved when querying the sheet.
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
