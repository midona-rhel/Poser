using System;
using System.Collections.Generic;
using System.Threading;

namespace Poser.Game.WorldObjects;

/// <summary>Thread-safe ownership ledger for the resource-load hook. Claims
/// are per spawned instance, not per session: an unsuccessful create rolls
/// back its pending token and a path remains handled while any live instance
/// still owns it.</summary>
internal sealed class VfxPathClaimOwner
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts =
        new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Acquire(string path)
    {
        path = path.Trim();
        lock (_gate)
        {
            _counts.TryGetValue(path, out var count);
            _counts[path] = count + 1;
        }
        return new Claim(this, path);
    }

    internal bool Contains(string path)
    {
        lock (_gate)
            return _counts.ContainsKey(path);
    }

    internal int Count(string path)
    {
        lock (_gate)
            return _counts.TryGetValue(path, out var count) ? count : 0;
    }

    internal bool HasClaims
    {
        get
        {
            lock (_gate)
                return _counts.Count != 0;
        }
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(path, out var count))
                return;
            if (count <= 1)
                _counts.Remove(path);
            else
                _counts[path] = count - 1;
        }
    }

    private sealed class Claim : IDisposable
    {
        private VfxPathClaimOwner? _owner;
        private readonly string _path;

        public Claim(VfxPathClaimOwner owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)
            ?.Release(_path);
    }
}
