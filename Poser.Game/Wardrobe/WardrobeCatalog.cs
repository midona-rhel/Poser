using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.Game.Wardrobe;

/// <summary>
/// The wardrobe from the game sheets: equippable items grouped by the slots
/// their category allows, the dyes in the game's own order, the facewear.
/// Read once on first use; the sheets do not change while the game runs.
/// </summary>
public sealed class WardrobeCatalog : IWardrobeCatalog
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private Dictionary<EquipSlot, List<WardrobeItem>>? _bySlot;
    private Dictionary<uint, WardrobeItem>? _byId;
    private List<DyeEntry>? _dyes;
    private Dictionary<byte, DyeEntry>? _dyeById;
    private List<FacewearEntry>? _facewear;

    private readonly object _gate = new();

    public WardrobeCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>Reads every sheet now, off the draw thread, so the first
    /// Equipment view pays nothing.</summary>
    public void Warm()
    {
        LoadItems();
        LoadDyes();
        _ = Facewear;
    }

    public IReadOnlyList<WardrobeItem> ItemsFor(EquipSlot slot)
    {
        LoadItems();
        return _bySlot!.TryGetValue(slot, out var items) ? items : Array.Empty<WardrobeItem>();
    }

    public WardrobeItem? Item(uint id)
    {
        LoadItems();
        return _byId!.TryGetValue(id, out var item) ? item : null;
    }

    public IReadOnlyList<DyeEntry> Dyes
    {
        get { LoadDyes(); return _dyes!; }
    }

    public DyeEntry? Dye(byte id)
    {
        LoadDyes();
        return _dyeById!.TryGetValue(id, out var dye) ? dye : null;
    }

    public IReadOnlyList<FacewearEntry> Facewear
    {
        get
        {
            if (_facewear is not null)
                return _facewear;
            var list = new List<FacewearEntry>();
            try
            {
                foreach (var row in _data.GetExcelSheet<Glasses>())
                {
                    string name = row.Name.ExtractText();
                    if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                        continue;
                    list.Add(new FacewearEntry(row.RowId, name, (uint)row.Icon));
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Wardrobe: the facewear sheet could not be read: {ex.Message}");
            }
            return _facewear = list;
        }
    }

    private void LoadDyes()
    {
        lock (_gate)
        {
        if (_dyes is not null)
            return;
        var list = new List<DyeEntry>();
        try
        {
            foreach (var row in _data.GetExcelSheet<Stain>())
            {
                string name = row.Name.ExtractText();
                if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;
                list.Add(new DyeEntry((byte)row.RowId, name, row.Color));
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Wardrobe: the dye sheet could not be read: {ex.Message}");
        }
        _dyeById = list.ToDictionary(dye => dye.Id);
        _dyes = list;
        }
    }

    /// <summary>The slots an equip-slot category admits, in Glamourer's
    /// slot numbering.</summary>
    private static IEnumerable<EquipSlot> SlotsOf(EquipSlotCategory category)
    {
        if (category.MainHand != 0) yield return EquipSlot.MainHand;
        if (category.OffHand != 0) yield return EquipSlot.OffHand;
        if (category.Head != 0) yield return EquipSlot.Head;
        if (category.Body != 0) yield return EquipSlot.Body;
        if (category.Gloves != 0) yield return EquipSlot.Hands;
        if (category.Legs != 0) yield return EquipSlot.Legs;
        if (category.Feet != 0) yield return EquipSlot.Feet;
        if (category.Ears != 0) yield return EquipSlot.Ears;
        if (category.Neck != 0) yield return EquipSlot.Neck;
        if (category.Wrists != 0) yield return EquipSlot.Wrists;
        if (category.FingerR != 0) yield return EquipSlot.RightFinger;
        if (category.FingerL != 0) yield return EquipSlot.LeftFinger;
    }

    private void LoadItems()
    {
        lock (_gate)
        {
        if (_bySlot is not null)
            return;
        var bySlot = new Dictionary<EquipSlot, List<WardrobeItem>>();
        var byId = new Dictionary<uint, WardrobeItem>();
        try
        {
            foreach (var row in _data.GetExcelSheet<Item>())
            {
                if (row.RowId == 0 || row.EquipSlotCategory.RowId == 0)
                    continue;
                string name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                ulong packed = row.ModelMain;
                var item = new WardrobeItem(
                    row.RowId, name, (uint)row.Icon, row.DyeCount,
                    (ushort)packed, (ushort)(packed >> 16), (byte)(packed >> 32));
                byId[row.RowId] = item;
                foreach (var slot in SlotsOf(row.EquipSlotCategory.Value))
                {
                    if (!bySlot.TryGetValue(slot, out var list))
                        bySlot[slot] = list = new List<WardrobeItem>();
                    list.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Wardrobe: the item sheet could not be read: {ex.Message}");
        }
        _byId = byId;
        _bySlot = bySlot;
        }
    }
}
