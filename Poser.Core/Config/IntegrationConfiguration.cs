using System;

namespace Poser.Config;

/// <summary>
/// External integration settings. The MCDF limits are hard validation caps
/// applied before any actor mutation — exceeding one is an explicit
/// failure, never a silent trim.
/// </summary>
[Serializable]
public class IntegrationConfiguration
{
    /// <summary>Maximum total expanded bytes of one package (default 2 GiB).</summary>
    public long McdfMaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum bytes of a single embedded file (default 512 MiB).</summary>
    public long McdfMaxFileBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Maximum embedded file entries (default 1024).</summary>
    public int McdfMaxFileCount { get; set; } = 1024;

    /// <summary>Maximum game paths across files and swaps (default 4096).</summary>
    public int McdfMaxGamePathCount { get; set; } = 4096;
}
