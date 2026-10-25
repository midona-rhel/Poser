using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Poser.Game.Tests.Natives;

/// <summary>
/// Checks common source spellings of hardcoded signatures, offsets, and native
/// writes. Generated names keep signatures and offsets in patchcheck; these
/// line-based checks catch likely violations but do not parse C# or prove coverage.
/// </summary>
public sealed class NativeSignatureSourceTests
{
    private static readonly Regex Pattern = new(@"""((?:[0-9A-F?]{2} ){3,}[0-9A-F?]{2})""", RegexOptions.Compiled);

    // An offset added to a pointer, a struct field's offset, or a named offset constant.
    private static readonly Regex Offset = new(
        @"\+\s*0x[0-9A-Fa-f]+|FieldOffset\(\s*0x|const\s+(?:int|nint)\b[^;]*=\s*0x", RegexOptions.Compiled);

    // Comments and string literals: offsets named in prose or log text.
    private static readonly Regex Prose = new(@"""(?:\\.|[^""\\])*""|//.*$", RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Poser.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Each line of the projects' production code outside the generated files
    /// and the Debug-only probe harness and bridge.</summary>
    private static IEnumerable<(string Where, string Line)> ProductionLines(params string[] projects)
    {
        string root = Root();
        return projects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                // Generated from patchcheck.
                && !path.EndsWith(".g.cs", StringComparison.Ordinal)
                // Debug-only probe harness and bridge.
                && !path.Contains($"{Path.DirectorySeparatorChar}Bridge{Path.DirectorySeparatorChar}")
                && !path.EndsWith(".Probe.cs", StringComparison.Ordinal)
                && !path.EndsWith(".ResetWatch.cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, i) =>
                ($"{Path.GetRelativePath(root, path)}:{i + 1}", line)));
    }

    [Fact]
    public void Owned_scene_object_cleanup_has_one_native_owner()
    {
        var cleanup = new Regex(@"->\s*(?:CleanupRender\s*\(|Dtor\s*\(\s*1\s*\))");
        var offenders = ProductionLines("Poser.Game")
            .Where(x => !x.Where.Contains("OwnedSceneObjectLifetime.cs:", StringComparison.Ordinal)
                && cleanup.IsMatch(Prose.Replace(x.Line, "")))
            .Select(x => $"{x.Where}: {x.Line.Trim()}").ToList();
        Assert.True(offenders.Count == 0,
            "Owned scene objects must use OwnedSceneObjectLifetime for cleanup-before-destruction:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Character_model_mutations_have_one_native_owner()
    {
        var assignment = new Regex(@"(?:ModelContainer\s*\.\s*ModelCharaId|->Models\s*\[[^\]]+\])\s*=(?!=)");
        var refCountWrite = new Regex(@"\bRefCount\s*(?:[+*/-]?=(?!=)|\+\+|--)");
        var offenders = ProductionLines("Poser.Game")
            .Where(x =>
            {
                var code = Prose.Replace(x.Line, "");
                return refCountWrite.IsMatch(code)
                    || (assignment.IsMatch(code) && !x.Where.Contains("CharacterModelNative.cs:", StringComparison.Ordinal));
            })
            .Select(x => $"{x.Where}: {x.Line.Trim()}").ToList();
        Assert.True(offenders.Count == 0,
            "Character model writes must use CharacterModelNative; reference counts must use native lifetime operations:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Production_code_scans_only_generated_signatures()
    {
        var offenders = ProductionLines("Poser", "Poser.Game", "Poser.Application", "Poser.UI", "Poser.Domain", "Poser.Documents")
            // A patch site's expected bytes are checked at a signed function's offset, not scanned.
            .Where(x => Pattern.IsMatch(x.Line) && !x.Line.Contains("SiteBytes", StringComparison.Ordinal))
            .Select(x => $"{x.Where}: {x.Line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "Byte patterns written into Poser's code; add them to patchcheck and use the generated name:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Game_code_reads_only_generated_offsets()
    {
        var offenders = ProductionLines("Poser.Game")
            .Where(x => Offset.IsMatch(Prose.Replace(x.Line, "")))
            .Select(x => $"{x.Where}: {x.Line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "Offsets written into Poser's code; add them to patchcheck and use the NativeFields name:\n"
            + string.Join("\n", offenders));
    }
}
