using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Poser.Services;

namespace Poser.Game.WorldObjects;

internal static class FurnitureCatalog
{
    internal static string PathFor(uint modelKey, bool indoors)
    {
        string model = modelKey.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        return $"bgcommon/hou/{(indoors ? "indoor" : "outdoor")}/general/{model}/asset/{(indoors ? "fun" : "gar")}_b0_m{model}.sgb";
    }

    internal static IReadOnlyList<WorldAsset> Load(IDataManager data)
    {
        var indoorCategories = new Dictionary<uint, string>();
        foreach (var row in data.GetExcelSheet<FurnitureCatalogItemList>())
        {
            var meta = row.Category.Value;
            indoorCategories[row.Item.RowId] = meta.Unknown0 switch
            {
                12 => "Indoor furnishings", 13 => "Tables", 14 => "Tabletop",
                15 => "Wall-mounted", 16 => "Rugs", _ => meta.Category.ToString(),
            };
        }
        var outdoorCategories = new Dictionary<uint, string>();
        foreach (var row in data.GetExcelSheet<YardCatalogItemList>())
            outdoorCategories[row.Item.RowId] = row.Category.Value.Category.ToString();

        var entries = new Dictionary<string, WorldAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.GetExcelSheet<HousingFurniture>())
            Add(row.Item.ValueNullable, row.ModelKey, true, indoorCategories);
        foreach (var row in data.GetExcelSheet<HousingYardObject>())
            Add(row.Item.ValueNullable, row.ModelKey, false, outdoorCategories);
        return entries.Values.OrderBy(x => x.Context).ThenBy(x => x.Label).ToArray();

        void Add(Item? item, uint model, bool indoors, Dictionary<uint, string> categories)
        {
            if (item is not { } value || model == 0) return;
            string name = value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) return;
            string path = PathFor(model, indoors);
            string category = categories.GetValueOrDefault(value.RowId, "Uncategorised");
            entries.TryAdd(path, new WorldAsset(name, path, name,
                $"Furniture · {(indoors ? "Indoor" : "Outdoor")} · {category}", value.Icon));
        }
    }
}
