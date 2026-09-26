using Poser.Config;

namespace Poser.Application.Settings;

public sealed class ReleaseNotesSession
{
    private readonly ConfigurationService _configuration;
    private bool _presented;

    public string Version { get; }
    public bool IsOpen { get; private set; }

    public ReleaseNotesSession(ConfigurationService configuration, Version version)
    {
        _configuration = configuration;
        Version = version.ToString();
        IsOpen = !System.Version.TryParse(configuration.Config.LastSeenReleaseVersion, out var seen)
            || Normalize(version) > Normalize(seen);
    }

    // A load alone is not presentation: hidden UI must not consume the notice.
    public void MarkPresented()
    {
        if (_presented || !IsOpen) return;
        var previous = _configuration.Config.LastSeenReleaseVersion;
        _configuration.Config.LastSeenReleaseVersion = Version;
        try { _configuration.Save(notify: false); }
        catch
        {
            _configuration.Config.LastSeenReleaseVersion = previous;
            throw;
        }
        _presented = true;
    }

    public void Dismiss() => IsOpen = false;

    private static Version Normalize(Version version) => new(
        version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
