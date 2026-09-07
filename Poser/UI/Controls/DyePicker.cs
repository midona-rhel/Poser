using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.UI.Controls;

// Equipment and furniture use the same named, colour-backed dye menu.
internal sealed class DyePicker(string id)
{
    private readonly Crystarium.SearchPicker<DyeEntry> _picker = new(id);
    private List<DyeEntry>? _rows;

    public void Open(string title, IWardrobeCatalog catalog, byte current)
    {
        if (_rows is null)
        {
            _rows = [new DyeEntry(0, "None", 0)];
            _rows.AddRange(catalog.Dyes);
        }
        _picker.Open(title, _rows, static dye => dye.Name,
            static dye => dye.Id.ToString(CultureInfo.InvariantCulture),
            current.ToString(CultureInfo.InvariantCulture), null,
            new PickerOptions<DyeEntry> { RowFill = static dye => dye.Id == 0 ? null : Color(dye.Color) });
    }

    public (string Owner, DyeEntry Item)? Draw() => _picker.Draw();

    public static void Cell(Crystarium.FormPairCell cell, string id,
        IWardrobeCatalog catalog, byte current, Action choose, Action clear)
    {
        var dye = current == 0 ? null : catalog.Dye(current);
        float height = Crystarium.ActiveTheme.Controls.WorkspaceHeight;
        ImGui.SetCursorScreenPos(cell.Center(height));
        Crystarium.ColorTile(id, dye is null ? null : Color(dye.Color),
            cell.Width / cell.Scale, height,
            () => { if (ImGui.GetIO().KeyCtrl) clear(); else choose(); },
            label: dye is null ? "None" : null, help: dye?.Name ?? "Choose dye");
    }

    public static Vector4 Color(uint rgb) => new(
        ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
}
