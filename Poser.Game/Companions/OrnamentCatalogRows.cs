using System;
using System.Globalization;
using Poser.Domain.Companions;

namespace Poser.Game.Companions;

internal static class OrnamentCatalogRows
{
    internal static CompanionEntry? Create(
        uint rowId, uint modelId, uint iconId, Func<uint, string> resolveName)
    {
        if (rowId == 0 || rowId > ushort.MaxValue || modelId == 0)
            return null;

        // Ornament.Singular can be empty for usable parasols. Brio resolves
        // ActStr instead; preserve that localized text rather than title-casing it.
        var name = resolveName(rowId);
        if (string.IsNullOrWhiteSpace(name))
            name = "Ornament " + rowId.ToString(CultureInfo.InvariantCulture);

        return new CompanionEntry(CompanionKind.Ornament, (ushort)rowId, name, iconId, modelId);
    }
}
