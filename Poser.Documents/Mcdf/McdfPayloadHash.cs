using System.Security.Cryptography;

namespace Poser.Documents.Mcdf;

// MCDF v1 has no algorithm tag. Old Mare/Poser files have 40-digit SHA-1
// hashes; current Lightless/Brio files have 64-digit BLAKE3 hashes.
internal sealed class McdfPayloadHash : IDisposable
{
    private readonly IncrementalHash? _legacy;
    private Blake3.Hasher _blake;

    public McdfPayloadHash(bool legacy = false)
    {
        if (legacy) _legacy = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        else _blake = Blake3.Hasher.New();
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_legacy != null) _legacy.AppendData(bytes);
        else _blake.Update(bytes);
    }

    public string Finish() => _legacy != null
        ? Convert.ToHexString(_legacy.GetHashAndReset())
        : _blake.Finalize().ToString().ToUpperInvariant();

    public void Dispose()
    {
        if (_legacy != null) _legacy.Dispose();
        else _blake.Dispose();
    }
}
