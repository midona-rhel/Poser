using System;
using System.Linq;
using Dalamud.Plugin;

namespace Poser.IPC;

/// <summary>
/// Status of an IPC service.
/// </summary>
public enum IPCStatus
{
    /// <summary>Service is available and ready to use.</summary>
    Available,
    /// <summary>Service is disabled in configuration.</summary>
    Disabled,
    /// <summary>Required plugin is not installed or not loaded.</summary>
    NotInstalled,
    /// <summary>Plugin API version is incompatible.</summary>
    VersionMismatch,
    /// <summary>An error occurred checking availability.</summary>
    Error
}

/// <summary>
/// Base class for IPC service integrations with external plugins.
/// Provides availability checking with caching.
/// </summary>
public abstract class IPCServiceBase : IDisposable
{
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(10);

    private readonly IDalamudPluginInterface _pluginInterface;
    private IPCStatus _lastStatus = IPCStatus.Error;
    private DateTime _lastCheckTime = DateTime.MinValue;

    /// <summary>
    /// Name of the plugin this service integrates with.
    /// </summary>
    protected abstract string PluginName { get; }

    /// <summary>
    /// Required API version (Major, Minor).
    /// Minor version is a minimum - higher minor versions are accepted.
    /// </summary>
    protected abstract (int Major, int Minor) RequiredVersion { get; }

    /// <summary>
    /// Whether this integration is enabled in configuration.
    /// </summary>
    protected virtual bool IsEnabledInConfig => true;

    /// <summary>
    /// Whether this service is available and ready to use.
    /// </summary>
    public bool IsAvailable => CheckStatus() == IPCStatus.Available;

    /// <summary>
    /// Current status of the service.
    /// </summary>
    public IPCStatus Status => CheckStatus();

    protected IPCServiceBase(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    /// <summary>
    /// Checks the availability status of the service.
    /// Results are cached for 10 seconds.
    /// </summary>
    /// <param name="force">Force a fresh check, ignoring cache.</param>
    protected IPCStatus CheckStatus(bool force = false)
    {
        // Return cached result if still valid
        if (!force && DateTime.Now - _lastCheckTime < CacheInterval)
        {
            return _lastStatus;
        }

        _lastCheckTime = DateTime.Now;

        try
        {
            // Check if disabled in config
            if (!IsEnabledInConfig)
            {
                _lastStatus = IPCStatus.Disabled;
                return _lastStatus;
            }

            // Check if plugin is installed and loaded
            var installed = _pluginInterface.InstalledPlugins
                .Any(p => p.Name == PluginName && p.IsLoaded);

            if (!installed)
            {
                _lastStatus = IPCStatus.NotInstalled;
                return _lastStatus;
            }

            // Check API version
            var version = GetAPIVersion();
            if (version == null)
            {
                _lastStatus = IPCStatus.Error;
                return _lastStatus;
            }

            var (major, minor) = version.Value;
            var (reqMajor, reqMinor) = RequiredVersion;

            // Major must match exactly, minor must be >= required
            if (major != reqMajor || minor < reqMinor)
            {
                _lastStatus = IPCStatus.VersionMismatch;
                return _lastStatus;
            }

            _lastStatus = IPCStatus.Available;
            return _lastStatus;
        }
        catch
        {
            _lastStatus = IPCStatus.Error;
            return _lastStatus;
        }
    }

    /// <summary>
    /// Gets the API version from the plugin.
    /// Override this to implement version checking.
    /// </summary>
    protected abstract (int Major, int Minor)? GetAPIVersion();

    /// <summary>
    /// Called to initialize IPC subscriptions.
    /// Called after service is constructed.
    /// </summary>
    protected virtual void Initialize() { }

    /// <summary>
    /// Disposes IPC resources.
    /// </summary>
    public virtual void Dispose() { }
}
