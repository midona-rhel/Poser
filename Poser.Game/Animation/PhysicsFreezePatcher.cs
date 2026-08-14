using System;
using Dalamud.Game;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;

namespace Poser.Game.Animation;

/// <summary>
/// Scan and raw-memory seam for <see cref="PhysicsFreezePatcher"/>. Narrow on
/// purpose: exactly the operations the patch needs, so tests can drive every
/// construction and patch stage without native memory.
/// </summary>
internal interface IPhysicsPatchNative
{
    bool TryScanFreezeSite(ISigScanner scanner, out nint address);
    byte[] ReadRaw(nint address, int length);
    void WriteRaw(nint address, byte[] data);
    MemoryProtection ChangePermission(nint address, int length, MemoryProtection newProtection);
}

internal sealed class DalamudPhysicsPatchNative : IPhysicsPatchNative
{
    public bool TryScanFreezeSite(ISigScanner scanner, out nint address) =>
        scanner.TryScanText(PhysicsFreezePatcher.FreezeSiteSignature, out address);

    public byte[] ReadRaw(nint address, int length) => MemoryHelper.ReadRaw(address, length);

    public void WriteRaw(nint address, byte[] data) => MemoryHelper.WriteRaw(address, data);

    public MemoryProtection ChangePermission(nint address, int length, MemoryProtection newProtection) =>
        MemoryHelper.ChangePermission(address, length, newProtection);
}

/// <summary>
/// The process-global physics-freeze code patch (Anamnesis' SkeletonFreezePhysics
/// site, via Brio's PhysicsService). This is a CODE patch, not a hook: two xmm
/// store instructions inside the physics update are overwritten with NOPs, so it
/// is inherently process-global and lives here rather than in any per-actor
/// owner.
///
/// Fail-closed contract: the capability is available only when the signature
/// resolves AND both instruction spans hold the expected store opcodes at
/// startup. Every patch and every restore re-reads the site first and refuses
/// to write over bytes it does not recognize (a foreign tool patching the same
/// site), so the saved originals can never be replaced by — or written over —
/// foreign bytes. A failed second write rolls the first back, so the site is
/// always fully patched or fully original and <see cref="IsFrozen"/> stays
/// truthful. A failed restore during dispose is reported explicitly as an
/// error log; it is never swallowed.
/// </summary>
internal sealed class PhysicsFreezePatcher : IDisposable
{
    // Anamnesis AddressService's SkeletonFreezePhysics signature (used verbatim
    // by Brio). The first four bytes of the signature ARE region 1's
    // instruction: movups [rax+0x10], xmm1.
    internal const string FreezeSiteSignature =
        "0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28";

    // Region 2 sits 0x9 bytes before the match: a 3-byte movups store in the
    // same physics write sequence (Brio/SimpleTweaks provenance). Its opcode
    // (0F 11) is pinned; the third byte is the ModRM operand byte, which is
    // not proven by the signature, so it is captured at startup rather than
    // hard-coded.
    private const int Region2Offset = 0x9;

    private static readonly byte[] ExpectedRegion1 = [0x0F, 0x11, 0x48, 0x10];
    private static readonly byte[] NopRegion1 = [0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] NopRegion2 = [0x90, 0x90, 0x90];

    private readonly IPluginLog _log;
    private readonly IPhysicsPatchNative _native;

    private readonly nint _address;
    private byte[] _original1 = [];
    private byte[] _original2 = [];
    private bool _disposed;

    public bool IsAvailable { get; }

    /// <summary>Stable detail for an unavailable patch site; null when available.</summary>
    public string? UnavailableDetail { get; }

    public bool IsFrozen { get; private set; }

    public PhysicsFreezePatcher(ISigScanner scanner, IPluginLog log)
        : this(scanner, log, new DalamudPhysicsPatchNative())
    {
    }

    internal PhysicsFreezePatcher(ISigScanner scanner, IPluginLog log, IPhysicsPatchNative native)
    {
        _log = log;
        _native = native;

        try
        {
            if (!native.TryScanFreezeSite(scanner, out _address) || _address == 0)
            {
                UnavailableDetail = "Physics freeze signature unavailable on this game version.";
            }
            else
            {
                var original1 = native.ReadRaw(_address, ExpectedRegion1.Length);
                var original2 = native.ReadRaw(_address - Region2Offset, NopRegion2.Length);
                if (!original1.AsSpan().SequenceEqual(ExpectedRegion1))
                {
                    UnavailableDetail =
                        "Physics freeze site holds unexpected instruction bytes.";
                }
                else if (original2 is not [0x0F, 0x11, _])
                {
                    UnavailableDetail =
                        "Physics freeze companion span holds unexpected instruction bytes.";
                }
                else
                {
                    _original1 = original1;
                    _original2 = original2;
                    IsAvailable = true;
                }
            }
        }
        catch (Exception ex)
        {
            UnavailableDetail = "Physics freeze site could not be read.";
            _log.Warning($"PhysicsFreezePatcher: {UnavailableDetail} {ex.Message}");
            return;
        }

        if (!IsAvailable)
            _log.Warning($"PhysicsFreezePatcher: {UnavailableDetail}");
    }

    public AnimationPortResult SetFrozen(bool frozen)
    {
        if (_disposed)
            return AnimationPortResult.Fail("Physics freeze patcher is disposed.");
        if (!IsAvailable)
            return AnimationPortResult.Fail(
                $"Physics freeze is unavailable: {UnavailableDetail}");
        if (frozen == IsFrozen)
            return AnimationPortResult.Ok();

        try
        {
            return frozen ? Freeze() : Unfreeze();
        }
        catch (Exception ex)
        {
            return AnimationPortResult.Fail($"Physics freeze failed: {ex.Message}");
        }
    }

    private AnimationPortResult Freeze()
    {
        // Refuse to patch over anything but the exact startup instructions:
        // if another tool wrote here since, NOPing would corrupt ITS state and
        // a later restore would resurrect stale bytes.
        if (!SiteMatches(_original1, _original2))
            return AnimationPortResult.Fail(
                "Physics freeze site changed since startup; refusing to patch.");

        // Both regions or neither: a fault after the first write rolls it
        // back, so a half-frozen simulation can never survive behind
        // IsFrozen == false.
        Replace(_address, NopRegion1);
        try
        {
            Replace(_address - Region2Offset, NopRegion2);
        }
        catch
        {
            Replace(_address, _original1);
            throw;
        }
        IsFrozen = true;
        return AnimationPortResult.Ok();
    }

    private AnimationPortResult Unfreeze()
    {
        // Restore only over our own NOPs. Foreign bytes here mean something
        // else wrote over the live patch; restoring originals blind would
        // destroy that write and desynchronize both tools.
        if (!SiteMatches(NopRegion1, NopRegion2))
            return AnimationPortResult.Fail(
                "Physics freeze site was overwritten while frozen; refusing to restore.");

        // A failed unpatch returns to the FULLY frozen state, so
        // IsFrozen == true stays truthful: if the second restore faults after
        // the first landed, the first region is re-patched before the failure
        // propagates.
        Replace(_address, _original1);
        try
        {
            Replace(_address - Region2Offset, _original2);
        }
        catch
        {
            Replace(_address, NopRegion1);
            throw;
        }
        IsFrozen = false;
        return AnimationPortResult.Ok();
    }

    private bool SiteMatches(byte[] expected1, byte[] expected2) =>
        _native.ReadRaw(_address, expected1.Length).AsSpan().SequenceEqual(expected1) &&
        _native.ReadRaw(_address - Region2Offset, expected2.Length).AsSpan().SequenceEqual(expected2);

    private void Replace(nint address, byte[] data)
    {
        var protection = _native.ChangePermission(
            address, data.Length, MemoryProtection.ExecuteReadWrite);
        try
        {
            _native.WriteRaw(address, data);
        }
        finally
        {
            // Page protection goes back even when the write faults.
            _native.ChangePermission(address, data.Length, protection);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (IsFrozen)
        {
            // The global code patch is this class's own and must come back
            // here; a failed unpatch is reported, never swallowed — the game
            // is left running patched code with no owner.
            var result = SetFrozen(false);
            if (!result.Success)
                _log.Error(
                    $"PhysicsFreezePatcher: failed to restore the physics patch on dispose: {result.Detail}");
        }
        _disposed = true;
    }
}
