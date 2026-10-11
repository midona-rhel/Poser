using System.Text.Json;

namespace Poser.Documents.Mcdf;

/// <summary>
/// MCDF v1 wire layout. The complete file is a legacy LZ4 stream whose
/// decompressed content is: ASCII <c>MCDF</c>, version byte 1, a
/// little-endian int32 JSON byte length, the UTF-8 JSON document, then the
/// raw file payloads immediately after the JSON in <c>Files</c> order.
/// Unknown JSON members are ignored; unknown versions fail explicitly.
/// </summary>
internal static class McdfWire
{
    // A JSON document larger than this is not a plausible character file.
    public const int MaxJsonBytes = 64 * 1024 * 1024;
    public const int ChunkSize = 81920;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };
}

internal sealed class McdfWireData
{
    public string Description { get; set; } = string.Empty;
    public string GlamourerData { get; set; } = string.Empty;
    public string CustomizePlusData { get; set; } = string.Empty;
    public string ManipulationData { get; set; } = string.Empty;
    public List<McdfWireFile> Files { get; set; } = new();
    public List<McdfWireSwap> FileSwaps { get; set; } = new();
}

internal sealed class McdfWireFile
{
    public List<string> GamePaths { get; set; } = new();
    // The MCDF v1 format declares each payload length as a JSON int32, so a
    // single payload is limited to int.MaxValue bytes (just under 2 GiB).
    // The writer refuses larger sources rather than emitting a wrapped length;
    // widening this would produce files other MCDF readers cannot open.
    public int Length { get; set; }
    public string Hash { get; set; } = string.Empty;
}

internal sealed class McdfWireSwap
{
    public List<string> GamePaths { get; set; } = new();
    public string FileSwapPath { get; set; } = string.Empty;
}
