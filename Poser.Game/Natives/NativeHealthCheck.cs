using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Diagnostics;

namespace Poser.Game.Natives;

/// <summary>
/// Checks every entry of the generated native registry (patchcheck's
/// poser-registry.tsv) against the running game: each function signature and patch
/// site found (a patch site's bytes as expected), each global found, each field offset
/// read from the game's own code and compared with the value Poser uses, and each hook
/// Poser created, live from the <see cref="HookLedger"/>. The scan runs once off the
/// framework thread; the summary and every problem go to the log.
/// </summary>
public sealed partial class NativeHealthCheck : INativeHealth
{
    private readonly ISigScanner _scanner;
    private readonly HookLedger _hooks;
    private readonly IPluginLog _log;
    private readonly long _moduleBase;
    private readonly object _lock = new();
    private IReadOnlyList<NativeHealthRow> _static = Array.Empty<NativeHealthRow>();
    private Dictionary<nint, string> _functionsByAddress = new();
    private DateTime _checkedAt;
    private bool _complete;

    public NativeHealthCheck(ISigScanner scanner, HookLedger hooks, IPluginLog log)
    {
        _scanner = scanner;
        _hooks = hooks;
        _log = log;
        _moduleBase = scanner.Module?.BaseAddress ?? 0;
        Recheck();
    }

    public void Recheck()
    {
        lock (_lock)
            _complete = false;
        Task.Run(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[Natives] the native check failed");
            }
        });
    }

    public NativeHealthReport Report
    {
        get
        {
            IReadOnlyList<NativeHealthRow> rows;
            Dictionary<nint, string> functions;
            DateTime at;
            bool complete;
            lock (_lock)
            {
                rows = _static;
                functions = _functionsByAddress;
                at = _checkedAt;
                complete = _complete;
            }
            return new NativeHealthReport(at, complete, HookRows(functions).Concat(rows).ToArray());
        }
    }

    [GeneratedRegex(@"check the bytes `([^`]+)` at function\+0x([0-9A-Fa-f]+)")]
    private static partial Regex PatchSite();

    private string Relative(long address) =>
        address == 0 ? "" : $"ffxiv+0x{address - _moduleBase:X}";

    private void Check()
    {
        var rows = new List<NativeHealthRow>(NativeRegistry.All.Length);
        var functions = new Dictionary<nint, string>();
        foreach (var entry in NativeRegistry.All)
        {
            switch (entry.Kind)
            {
                case NativeKind.Function:
                    rows.Add(CheckFunction(entry, functions));
                    break;
                case NativeKind.Offset:
                    rows.Add(CheckOffset(entry));
                    break;
                case NativeKind.Data:
                    if (entry.TryResolve(_scanner, out var data) && data != 0)
                    {
                        functions.TryAdd((nint)data, entry.Name);
                        rows.Add(Row("Data", entry, NativeHealthState.Ok, Relative(data), entry.Note));
                    }
                    else
                        rows.Add(Row("Data", entry, NativeHealthState.Missing, "", "pattern not found"));
                    break;
                case NativeKind.Hook:
                    break; // read live from the ledger
                default:
                    rows.Add(Row("Not checkable", entry, NativeHealthState.Unchecked, "",
                        entry.Status.StartsWith("unsigned:", StringComparison.Ordinal)
                            ? entry.Status["unsigned:".Length..]
                            : string.IsNullOrEmpty(entry.Note) ? entry.Status : entry.Note));
                    break;
            }
        }
        lock (_lock)
        {
            _static = rows;
            _functionsByAddress = functions;
            _checkedAt = DateTime.Now;
            _complete = true;
        }
        LogSummary(Report);
    }

    private NativeHealthRow CheckFunction(NativeEntry entry, Dictionary<nint, string> functions)
    {
        if (!entry.TryResolve(_scanner, out var address) || address == 0)
            return Row("Functions", entry, NativeHealthState.Missing, "", "signature not found");
        functions.TryAdd((nint)address, entry.Name);
        if (PatchSite().Match(entry.Note) is { Success: true } site)
        {
            int offset = Convert.ToInt32(site.Groups[2].Value, 16);
            return NativeCode.Matches((nint)address + offset, site.Groups[1].Value)
                ? Row("Patch sites", entry, NativeHealthState.Ok, Relative(address + offset),
                    $"the bytes at function+0x{offset:X} are as expected")
                : Row("Patch sites", entry, NativeHealthState.Missing, Relative(address + offset),
                    $"the bytes at function+0x{offset:X} changed: the patch is refused");
        }
        return Row("Functions", entry, NativeHealthState.Ok, Relative(address), entry.Note);
    }

    private NativeHealthRow CheckOffset(NativeEntry entry)
    {
        if (!NativeRegistry.Offsets.TryGetValue(entry.Name, out var pattern))
            return Row("Offsets", entry, NativeHealthState.Unchecked, "", "no pattern in the registry");
        if (!NativeOffsets.TryResolve(_scanner, pattern, out var offset))
            return Row("Offsets", entry, NativeHealthState.Missing, "", "the game code that reads it wasn't found");
        return offset == pattern.Analysed
            ? Row("Offsets", entry, NativeHealthState.Ok, $"0x{offset:X}", entry.Note)
            : Row("Offsets", entry, NativeHealthState.Moved, $"0x{offset:X}",
                $"the game now uses 0x{offset:X}; Poser uses 0x{pattern.Analysed:X}");
    }

    private static NativeHealthRow Row(string group, NativeEntry entry, NativeHealthState state, string value, string detail) =>
        new(group, entry.Name, state, value, detail, entry.Consumers);

    /// <summary>
    /// Each hook Poser made, by the registry name of its target where the address is a
    /// signed function's, and each hook site the registry lists that made none: a
    /// feature not used yet creates its hook later; a failed signature makes none.
    /// </summary>
    private IEnumerable<NativeHealthRow> HookRows(Dictionary<nint, string> functions)
    {
        var made = _hooks.Entries;
        var rows = new List<NativeHealthRow>(made.Count + 4);
        var createdPerFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hook in made)
        {
            string name = functions.TryGetValue(hook.Address, out var known) ? known : hook.Delegate;
            string file = hook.CreatedBy;
            createdPerFile[file] = createdPerFile.GetValueOrDefault(file) + 1;
            var (state, detail) = hook switch
            {
                { Failure: { } failure } => (NativeHealthState.Missing, $"couldn't be created: {failure}"),
                { Hook.IsDisposed: true } => (NativeHealthState.Off, "removed"),
                { Hook.IsEnabled: true } => (NativeHealthState.Ok, "running"),
                _ => (NativeHealthState.Off, "created, not running now"),
            };
            rows.Add(new NativeHealthRow("Hooks", name, state, Relative(hook.Address),
                $"{detail} ({hook.Delegate})", file));
        }
        // The registry's hook sites by file; a file whose hooks were all made is done.
        foreach (var site in NativeRegistry.All.Where(e => e.Kind == NativeKind.Hook)
                     .GroupBy(e => e.Consumers.Split(':')[0], StringComparer.OrdinalIgnoreCase))
        {
            int expected = site.Count();
            int createdHere = createdPerFile.GetValueOrDefault(site.Key)
                + createdPerFile.GetValueOrDefault(System.IO.Path.GetFileNameWithoutExtension(site.Key));
            for (int i = createdHere; i < expected; i++)
                rows.Add(new NativeHealthRow("Hooks", site.Key, NativeHealthState.Off, "",
                    "not created: its feature hasn't started, or its target wasn't found (see Functions)",
                    site.ElementAt(i).Consumers));
        }
        return rows;
    }

    private void LogSummary(NativeHealthReport report)
    {
        var counts = report.Rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        int hooks = report.Rows.Count(r => r.Group == "Hooks" && r.Value.Length > 0);
        int running = report.Rows.Count(r => r.Group == "Hooks" && r.State == NativeHealthState.Ok);
        _log.Information(
            $"[Natives] {report.Rows.Count} dependencies: {counts.GetValueOrDefault(NativeHealthState.Ok)} ok, "
            + $"{counts.GetValueOrDefault(NativeHealthState.Missing)} missing, "
            + $"{counts.GetValueOrDefault(NativeHealthState.Moved)} moved, "
            + $"{counts.GetValueOrDefault(NativeHealthState.Unchecked)} not checkable; "
            + $"{hooks} hooks made, {running} running.");
        foreach (var row in report.Rows.Where(r => r.State is NativeHealthState.Missing or NativeHealthState.Moved))
            _log.Warning($"[Natives] {row.State}: {row.Group} {row.Name} {row.Value} — {row.Detail} (used by {row.UsedBy})");
    }
}
