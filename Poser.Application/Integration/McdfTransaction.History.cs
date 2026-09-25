using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

public sealed partial class McdfTransaction
{
    private sealed record HistoryPackage(Guid Id, SessionGeneration Session, McdfPackage Package);
    private readonly Dictionary<string, HistoryPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyDirectories = new(StringComparer.OrdinalIgnoreCase);

    // Session ownership is deliberate: clearing/evicting one history entry must
    // not release a package another actor or inverse still references.
    internal IntegrationValue<Guid> RetainHistory(string directory)
    {
        if (!_packages.TryGetValue(directory, out var package)
            || package.Session != _sessions.ActiveSessionGeneration || !_directories.ContainsKey(directory))
            return IntegrationValue<Guid>.Fail("The imported appearance resources are no longer available.");
        _historyDirectories.Add(directory);
        return IntegrationValue<Guid>.Ok(package.Id);
    }

    internal IntegrationResult RestoreHistory(ActorId actor, Guid resource, string sourcePath)
    {
        var retained = _packages.Values.FirstOrDefault(p => p.Id == resource
            && p.Session == _sessions.ActiveSessionGeneration
            && _historyDirectories.Contains(p.Package.OperationDirectory));
        if (retained == null || !_directories.ContainsKey(retained.Package.OperationDirectory))
            return IntegrationResult.Fail("The retained character-file resources are no longer available.");
        return BeginImport(actor, sourcePath, retained.Package);
    }

    internal string? ReleaseHistoryResources()
    {
        _historyDirectories.Clear();
        var failures = new List<string>();
        foreach (var directory in _packages.Keys.ToArray())
        {
            if (_owner.UsesDirectory(directory) || _inFlight?.OperationDirectory?.Path == directory) continue;
            var released = DeleteOperationDirectory(directory);
            if (!released.Success) failures.Add(released.Detail ?? "Character-file resource cleanup failed.");
        }
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }
}
