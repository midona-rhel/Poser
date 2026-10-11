namespace Poser.Application.Settings;

public sealed class ReleaseNotesSession
{
    private readonly ConfigurationService _configuration;
    private bool _presented;
    private readonly IReadOnlyList<ReleaseNotesEntry> _history;

    public string Version { get; }
    public bool IsOpen { get; private set; }
    public bool ShowingHistory { get; private set; }
    public int ViewRevision { get; private set; }
    public IReadOnlyList<ReleaseNotesEntry> Entries { get; private set; }

    public ReleaseNotesSession(ConfigurationService configuration, Version version)
    {
        _configuration = configuration;
        Version = version.ToString();
        var current = Normalize(version);
        bool hasSeen = System.Version.TryParse(configuration.Config.LastSeenReleaseVersion, out var seen);
        var previous = hasSeen ? Normalize(seen!) : null;
        _history = ReleaseNotesHistory.Entries.Where(entry => Normalize(entry.Version) <= current)
            .OrderByDescending(entry => Normalize(entry.Version)).ToArray();
        // Freeze the upgrade range before MarkPresented advances persistent state.
        Entries = _history.Where(entry => previous is null || Normalize(entry.Version) > previous).ToArray();
        IsOpen = previous is null || current > previous;
        _presented = !IsOpen;
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
    public void Open()
    {
        ShowingHistory = true;
        Entries = _history;
        ViewRevision++;
        IsOpen = true;
    }

    private static Version Normalize(Version version) => new(
        version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
