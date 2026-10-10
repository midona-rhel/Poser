using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Game.Wardrobe;

namespace Poser.Lifecycle;

/// <summary>Catalog startup belongs to the host, not to constructing or
/// drawing an appearance pane. The warm-up reads through provider-owned
/// catalogs, so it is stopped before the provider goes; a sheet already being
/// read gets a short grace period rather than an unbounded join.</summary>
internal sealed class AppearanceCatalogWarmup(
    WardrobeCatalog wardrobe, CustomizeCatalog customize, IPluginLog log) : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private Task? _warmup;
    private bool _stopped;

    public void Start()
    {
        var token = _cancel.Token;
        _warmup = Task.Run(() =>
        {
            try
            {
                wardrobe.Warm(token);
                customize.Warm(token);
            }
            catch (OperationCanceledException)
            {
                // Unload: the catalogs load on demand if anything still asks.
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Appearance catalog warm-up failed; catalogs will retry on demand.");
            }
        });
    }

    /// <summary>Called first by the host's unload, and again by the
    /// provider's dispose.</summary>
    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _cancel.Cancel();
        _warmup?.Wait(TimeSpan.FromSeconds(1));
        _cancel.Dispose();
    }
}
