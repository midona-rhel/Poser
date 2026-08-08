using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Poser.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// The embedded gobo library: housing-window textures the game already ships,
/// read from a two-column <c>path,name</c> CSV (Ktisis' GoboReader format).
/// </summary>
internal static class GoboLibrary
{
    private const string ResourceName = "Poser.Game.Lighting.Data.gobos.csv";

    public static IReadOnlyList<GoboEntry> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);
            return stream == null
                ? Array.Empty<GoboEntry>()
                : Parse(stream);
        }
        catch (Exception)
        {
            return Array.Empty<GoboEntry>();
        }
    }

    private static List<GoboEntry> Parse(Stream stream)
    {
        var result = new List<GoboEntry>();
        using var reader = new StreamReader(stream, new UTF8Encoding());

        // The header line is consumed unconditionally; a file whose first line
        // is not two columns is not this format at all.
        var line = reader.ReadLine();
        if (line == null || line.Split(',').Length != 2)
            return result;

        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim().Length == 0)
                continue;
            var split = line.Split(',');
            if (split.Length != 2)
                continue;
            result.Add(new GoboEntry(split[0].Trim(), split[1].Trim()));
        }

        return result;
    }
}
