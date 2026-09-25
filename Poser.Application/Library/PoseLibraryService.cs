using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Poser.Config;

namespace Poser.Library;

/// <inheritdoc cref="IPoseLibraryService"/>
public sealed class PoseLibraryService : IPoseLibraryService
{
    private static readonly PoseLibrarySnapshot EmptySnapshot = new()
    {
        Revision = 0,
        Generation = 0,
        TerminalResult = PoseLibraryScanResult.Initial,
        Entries = [],
        Folders = [],
        Sources = []
    };

    private readonly ConfigurationService _config;
    private readonly LibraryScanner _scanner;
    private readonly int _maxSources;
    private readonly object _sync = new();

    private PoseLibrarySnapshot _snapshot = EmptySnapshot;
    private string _sourceSignature;
    private CancellationTokenSource? _scanCancellation;
    private long _generation;
    private bool _scanning;
    private bool _scanQueued;
    private bool _disposed;

    public PoseLibraryService(ConfigurationService config)
        : this(config, new LibraryScanner())
    {
    }

    public PoseLibraryService(
        ConfigurationService config,
        LibraryScanner scanner,
        int maxSources = PoseLibraryLimits.MaxSources)
    {
        _config = config;
        _scanner = scanner;
        _maxSources = Math.Clamp(maxSources, 1, PoseLibraryLimits.MaxSources);
        _sourceSignature = BuildSourceSignature();
        _snapshot = new PoseLibrarySnapshot
        {
            Revision = 0,
            Generation = 0,
            TerminalResult = PoseLibraryScanResult.Initial,
            Entries = [],
            Folders = [],
            Sources = CaptureSources(PoseLibrarySourceHealth.Unscanned),
            SkippedSourceCount = Math.Max(0, _config.Config.Library.Sources.Count - _maxSources)
        };
        _config.OnConfigurationChanged += OnConfigurationChanged;
    }

    public PoseLibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsScanning
    {
        get
        {
            lock (_sync)
                return _scanning;
        }
    }

    public void RequestScan()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _generation++;
            if (_scanning)
            {
                _scanQueued = true;
                _scanCancellation?.Cancel();
                return;
            }

            _scanning = true;
            _scanCancellation = new CancellationTokenSource();
        }

        _ = Task.Run(ScanLoop);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _scanQueued = false;
            _scanCancellation?.Cancel();
        }

        _config.OnConfigurationChanged -= OnConfigurationChanged;
    }

    // A config save fires for every setting; source identity, enabled state,
    // path, and order are the only changes that invalidate the snapshot.
    private void OnConfigurationChanged()
    {
        var signature = BuildSourceSignature();
        lock (_sync)
        {
            if (_disposed || string.Equals(signature, _sourceSignature, StringComparison.Ordinal))
                return;
            _sourceSignature = signature;
        }

        RequestScan();
    }

    private string BuildSourceSignature()
    {
        var builder = new StringBuilder();
        foreach (var source in _config.Config.Library.Sources)
        {
            builder.Append(source.Enabled ? '1' : '0');
            builder.Append('\0');
            builder.Append(source.Name);
            builder.Append('\0');
            builder.Append(source.Path);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private void ScanLoop()
    {
        while (true)
        {
            CancellationToken token;
            long generation;
            lock (_sync)
            {
                if (_disposed || _scanCancellation is null)
                {
                    _scanning = false;
                    return;
                }

                _scanQueued = false;
                token = _scanCancellation.Token;
                generation = _generation;
            }

            try
            {
                var sources = _config.Config.Library.Sources
                    .Select(source => new LibrarySourceSpec(source.Name, source.Path, source.Enabled)).ToArray();
                var result = _scanner.Scan(sources, generation, Snapshot.Revision + 1, token);
                lock (_sync)
                {
                    if (!_disposed && generation == _generation && !token.IsCancellationRequested)
                        Volatile.Write(ref _snapshot, result);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation abandons the whole pass; no partial result is
                // ever handed to the reader.
            }
            catch (Exception ex)
            {
                PublishFailure(generation, token, BoundDetail(
                    "The library scan failed: " + ex.Message));
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    _scanning = false;
                    _scanCancellation?.Dispose();
                    _scanCancellation = null;
                    return;
                }

                if (_scanQueued)
                {
                    _scanCancellation?.Dispose();
                    _scanCancellation = new CancellationTokenSource();
                    continue;
                }

                _scanning = false;
                _scanCancellation?.Dispose();
                _scanCancellation = null;
                return;
            }
        }
    }

    private IReadOnlyList<PoseLibrarySourceSnapshot> CaptureSources(
        PoseLibrarySourceHealth health,
        string? detail = null)
    {
        var sources = _config.Config.Library.Sources;
        var result = new List<PoseLibrarySourceSnapshot>(Math.Min(sources.Count, _maxSources));
        for (var i = 0; i < Math.Min(sources.Count, _maxSources); i++)
        {
            var source = sources[i];
            var state = source.Enabled ? health : PoseLibrarySourceHealth.Disabled;
            result.Add(new PoseLibrarySourceSnapshot
            {
                Index = i,
                Name = source.Name,
                Path = source.Path,
                Enabled = source.Enabled,
                Health = state,
                Detail = state == PoseLibrarySourceHealth.Disabled
                    ? "Source is disabled."
                    : detail ?? string.Empty
            });
        }
        return result;
    }

    private void PublishFailure(
        long generation,
        CancellationToken cancellation,
        string detail)
    {
        if (cancellation.IsCancellationRequested)
            return;
        lock (_sync)
        {
            if (_disposed || generation != _generation)
                return;
            var revision = _snapshot.Revision + 1;
            Volatile.Write(ref _snapshot, new PoseLibrarySnapshot
            {
                Revision = revision,
                Generation = generation,
                TerminalResult = PoseLibraryScanResult.Failure,
                Entries = [],
                Folders = [],
                Sources = CaptureSources(PoseLibrarySourceHealth.Failed, detail),
                SkippedSourceCount = Math.Max(0, _config.Config.Library.Sources.Count - _maxSources)
            });
        }
    }

    private static string BoundDetail(string detail) =>
        detail.Length <= 4096 ? detail : detail[..4096];
}
