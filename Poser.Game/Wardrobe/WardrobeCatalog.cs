using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Domain.Integration;
using Poser.Application.Catalog;
using Poser.Game.Core;

namespace Poser.Game.Wardrobe;

/// <summary>
/// The wardrobe from the game sheets: equippable items grouped by the slots
/// their category allows, the dyes in the game's own order, the facewear.
/// Each list is read once, on first use or by the startup warm-up, with
/// <see cref="GameDataBuild"/>. A sheet that fails keeps the rows read before
/// it and its reason; the list's picker shows the reason.
/// </summary>
public sealed class WardrobeCatalog : IWardrobeCatalog
{
    private readonly IDataManager _data;
    private readonly GameDataBuild _items;
    private readonly GameDataBuild _dyeList;
    private readonly GameDataBuild _facewearList;
    private Dictionary<EquipSlot, List<WardrobeItem>> _bySlot = new();
    private Dictionary<uint, WardrobeItem> _byId = new();
    private List<DyeEntry> _dyes = new();
    private Dictionary<byte, DyeEntry> _dyeById = new();
    private List<FacewearEntry> _facewear = new();

    public WardrobeCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _items = new("item", LoadItems, log);
        _dyeList = new("dye", LoadDyes, log);
        _facewearList = new("facewear", LoadFacewear, log);
    }

    /// <summary>Reads equipment, dyes and facewear on the calling thread, for
    /// the startup warm-up. Checks cancellation between the lists.</summary>
    public void Warm(CancellationToken cancel)
    {
        _items.BuildNow();
        cancel.ThrowIfCancellationRequested();
        _dyeList.BuildNow();
        cancel.ThrowIfCancellationRequested();
        _facewearList.BuildNow();
    }

    public string? LoadFailure(WardrobeList list) => Build(list).Failure;

    public void Retry(WardrobeList list) => Build(list).ForgetFailure();

    private GameDataBuild Build(WardrobeList list) => list switch
    {
        WardrobeList.Items => _items,
        WardrobeList.Dyes => _dyeList,
        _ => _facewearList,
    };

    public IReadOnlyList<WardrobeItem> ItemsFor(EquipSlot slot)
    {
        _items.BuildNow();
        return _bySlot.TryGetValue(slot, out var items) ? items : Array.Empty<WardrobeItem>();
    }

    public WardrobeItem? Item(uint id)
    {
        _items.BuildNow();
        return _byId.TryGetValue(id, out var item) ? item : null;
    }

    public IReadOnlyList<DyeEntry> Dyes
    {
        get { _dyeList.BuildNow(); return _dyes; }
    }

    public DyeEntry? Dye(byte id)
    {
        _dyeList.BuildNow();
        return _dyeById.TryGetValue(id, out var dye) ? dye : null;
    }

    public IReadOnlyList<FacewearEntry> Facewear
    {
        get { _facewearList.BuildNow(); return _facewear; }
    }

    // Each list is published before it is filled, so a failing sheet keeps
    // the rows read before it. Readers wait in BuildNow while it is filled.

    private void LoadFacewear()
    {
        var list = _facewear = new List<FacewearEntry>();
        foreach (var row in _data.GetExcelSheet<Glasses>())
        {
            string name = row.Name.ExtractText();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;
            list.Add(new FacewearEntry(row.RowId, name, (uint)row.Icon));
        }
    }

    private void LoadDyes()
    {
        var list = _dyes = new List<DyeEntry>();
        var byId = _dyeById = new Dictionary<byte, DyeEntry>();
        foreach (var row in _data.GetExcelSheet<Stain>())
        {
            string name = row.Name.ExtractText();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;
            var dye = new DyeEntry((byte)row.RowId, name, row.Color);
            list.Add(dye);
            byId[dye.Id] = dye;
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
        var bySlot = _bySlot = new Dictionary<EquipSlot, List<WardrobeItem>>();
        var byId = _byId = new Dictionary<uint, WardrobeItem>();
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
}
