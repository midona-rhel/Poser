namespace Poser.Game;

/// <summary>
/// Destruction bookkeeping fed by the native Character finalize hook. This is
/// the production transition logic shared by the real adapter and driven
/// directly by tests; the hook only forwards (address, index) into it.
/// </summary>
internal sealed class SpawnLifetimeStamps
{
    // Character objects are pool-allocated, so distinct destroyed addresses
    // stay small in practice; the cap only bounds a pathological session.
    // On overflow the maps clear while the sequence keeps rising, and the
    // clear raises the unknown-entry floor to the current sequence: every
    // pre-clear stamp — including the implicit stamp 0 of a never-destroyed
    // entry — is strictly below the floor, so a post-clear resolve can only
    // mismatch a stored descriptor (refusal/retire of our own records),
    // never compare equal and claim a foreign object.
    private const int MaxTrackedAddresses = 8192;

    private readonly object _gate = new();
    private readonly Dictionary<nint, ulong> _byAddress = new();
    private readonly Dictionary<ushort, ulong> _byIndex = new();
    private ulong _sequence;
    private ulong _clearFloor;

    public void NoteDestroyed(nint address, ushort? index)
    {
        lock (_gate)
        {
            _sequence++;
            if (_byAddress.Count >= MaxTrackedAddresses
                && !_byAddress.ContainsKey(address))
            {
                _byAddress.Clear();
                _byIndex.Clear();
                _clearFloor = _sequence;
            }
            _byAddress[address] = _sequence;
            if (index is { } known)
                _byIndex[known] = _sequence;
        }
    }

    public ulong StampFor(nint address)
    {
        lock (_gate)
        {
            return _byAddress.GetValueOrDefault(address, _clearFloor);
        }
    }

    public ulong IndexStampFor(ushort index)
    {
        lock (_gate)
        {
            return _byIndex.GetValueOrDefault(index, _clearFloor);
        }
    }
}
