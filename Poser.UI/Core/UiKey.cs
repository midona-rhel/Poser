using System;
using System.Buffers.Binary;

namespace Poser.UI;

internal enum UiKeyKind : byte
{
    None = 0,
    Int = 1,
    Guid = 2,
    Literal = 3,
    // Reserved: type-discriminated compound domain keys (actor + bone and
    // friends). The factory lands with the first dynamic-row consumer.
    Compound = 4,
}

/// <summary>
/// Allocation-free tagged identity for a component scope. Literal keys hold a
/// string reference compared by ordinal value, not by reference: authors must
/// pass compile-time constants so no string is allocated per frame.
/// </summary>
public readonly struct UiKey : IEquatable<UiKey>
{
    private readonly ulong _lo;
    private readonly ulong _hi;
    private readonly string? _text;
    private readonly UiKeyKind _kind;

    private UiKey(UiKeyKind kind, ulong lo, ulong hi, string? text)
    {
        _kind = kind;
        _lo = lo;
        _hi = hi;
        _text = text;
    }

    public static UiKey None => default;

    internal UiKeyKind Kind => _kind;

    public static implicit operator UiKey(int value) => new(UiKeyKind.Int, unchecked((ulong)(long)value), 0, null);

    public static implicit operator UiKey(long value) => new(UiKeyKind.Int, unchecked((ulong)value), 0, null);

    public static implicit operator UiKey(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return new UiKey(
            UiKeyKind.Guid,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            null);
    }

    public static implicit operator UiKey(string? value) =>
        value is null ? default : new UiKey(UiKeyKind.Literal, 0, 0, value);

    public bool Equals(UiKey other)
    {
        if (_kind != other._kind)
            return false;

        return _kind switch
        {
            UiKeyKind.None => true,
            UiKeyKind.Literal => string.Equals(_text, other._text, StringComparison.Ordinal),
            _ => _lo == other._lo && _hi == other._hi,
        };
    }

    public override bool Equals(object? obj) => obj is UiKey other && Equals(other);

    public override int GetHashCode() => _kind switch
    {
        UiKeyKind.None => 0,
        UiKeyKind.Literal => HashCode.Combine(_kind, _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text)),
        _ => HashCode.Combine(_kind, _lo, _hi),
    };

    /// <summary>
    /// Folds the COMPLETE key representation into a path hash. Never routed
    /// through <see cref="GetHashCode"/>: that collapses a 64-bit payload to
    /// 32 bits, and two rows whose ids happen to fold together would then
    /// share one interaction identity.
    /// </summary>
    internal ulong HashInto(ulong hash)
    {
        hash = UiRoot.Mix(hash, (byte)_kind);
        switch (_kind)
        {
            case UiKeyKind.None:
                return hash;
            case UiKeyKind.Literal:
                string text = _text ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                    hash = UiRoot.Mix(hash, text[i]);
                return UiRoot.Mix(hash, (ulong)(uint)text.Length);
            default:
                return UiRoot.Mix(UiRoot.Mix(hash, _lo), _hi);
        }
    }

    public static bool operator ==(UiKey left, UiKey right) => left.Equals(right);

    public static bool operator !=(UiKey left, UiKey right) => !left.Equals(right);

    // Diagnostics only: allocates, so never call on a warm path.
    public override string ToString() => _kind switch
    {
        UiKeyKind.None => "none",
        UiKeyKind.Int => unchecked((long)_lo).ToString(),
        UiKeyKind.Guid => $"{_hi:x16}{_lo:x16}",
        UiKeyKind.Literal => _text ?? string.Empty,
        _ => _kind.ToString(),
    };
}
