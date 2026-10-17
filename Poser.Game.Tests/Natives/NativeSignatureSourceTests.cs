using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Poser.Game.Tests.Natives;

/// <summary>
/// Every signature Poser scans is a generated name from patchcheck
/// (NativeSignatures.g.cs), so the native check and patchcheck see it. A byte pattern
/// written into the code would be invisible to both.
/// </summary>
public sealed class NativeSignatureSourceTests
{
    private static readonly Regex Pattern = new(@"""((?:[0-9A-F?]{2} ){3,}[0-9A-F?]{2})""", RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Poser.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Production_code_scans_only_generated_signatures()
    {
        string root = Root();
        var offenders = new[] { "Poser", "Poser.Game", "Poser.Application", "Poser.UI", "Poser.Domain", "Poser.Documents" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                // Generated from patchcheck.
                && !path.EndsWith(".g.cs", StringComparison.Ordinal)
                // Debug-only probe harness and bridge.
                && !path.Contains($"{Path.DirectorySeparatorChar}Bridge{Path.DirectorySeparatorChar}")
                && !path.EndsWith(".Probe.cs", StringComparison.Ordinal)
                && !path.EndsWith(".ResetWatch.cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, number: i + 1)))
            // A patch site's expected bytes are checked at a signed function's offset, not scanned.
            .Where(x => Pattern.IsMatch(x.line) && !x.line.Contains("SiteBytes", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(root, x.path)}:{x.number}: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "Byte patterns written into Poser's code; add them to patchcheck and use the generated name:\n"
            + string.Join("\n", offenders));
    }
}
