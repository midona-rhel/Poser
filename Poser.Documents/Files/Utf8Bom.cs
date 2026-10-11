using System;

namespace Poser.Documents.Files;

internal static class Utf8Bom
{
    /// <summary>
    /// The document bytes without a leading UTF-8 preamble. Windows editors
    /// and tools that read with a <c>StreamReader</c> (Brio, Anamnesis) write
    /// or tolerate one, but <c>Utf8JsonReader</c> rejects it as invalid JSON.
    /// </summary>
    internal static ReadOnlySpan<byte> Strip(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes;
}
