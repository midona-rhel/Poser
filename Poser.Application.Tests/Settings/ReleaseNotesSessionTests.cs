using Poser.Application.Settings;
using Poser.Config;

namespace Poser.Application.Tests.Settings;

public sealed class ReleaseNotesSessionTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("invalid", true)]
    [InlineData("0.9.9.0", true)]
    [InlineData("0.9.10", false)]
    [InlineData("0.9.10.0", false)]
    [InlineData("0.9.11.0", false)]
    public void Only_an_unseen_newer_version_opens(string? seen, bool expected)
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = seen;
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 10, 0));
        Assert.Equal(expected, session.IsOpen);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Presentation_persists_once_without_configuration_notification_and_reload_does_not_reopen()
    {
        var store = new MemoryStore();
        var config = new ConfigurationService(store);
        var notifications = 0;
        config.OnConfigurationChanged += () => notifications++;
        var session = new ReleaseNotesSession(config, new(0, 9, 10, 0));
        session.MarkPresented();
        session.MarkPresented();
        Assert.True(session.IsOpen);
        Assert.Equal(1, store.Saves);
        Assert.Equal(0, notifications);
        session.Dismiss();
        Assert.False(session.IsOpen);
        Assert.False(new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 10, 0)).IsOpen);
        Assert.True(new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 11, 0)).IsOpen);
    }

    [Fact]
    public void Explicit_reopening_preserves_the_seen_version_without_saving_again()
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = "0.9.11.0";
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 10, 0));
        session.Open();
        session.MarkPresented();
        Assert.True(session.IsOpen);
        Assert.Equal("0.9.11.0", store.Config.LastSeenReleaseVersion);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Failure_does_not_consume_the_version_and_can_retry()
    {
        var store = new MemoryStore { Fail = true };
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 10, 0));
        Assert.Throws<IOException>(session.MarkPresented);
        Assert.Null(store.Config.LastSeenReleaseVersion);
        store.Fail = false;
        session.MarkPresented();
        Assert.Equal("0.9.10.0", store.Config.LastSeenReleaseVersion);
    }

    [Fact]
    public void Upgrade_includes_every_missed_release_but_not_seen_or_future_versions()
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = "0.9.10.0";
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 12, 0));
        Assert.Equal(new[] { "0.9.12", "0.9.11" }, session.Entries.Select(entry => entry.Version.ToString()));
        Assert.False(session.ShowingHistory);
        session.MarkPresented();
        Assert.Equal("0.9.12.0", store.Config.LastSeenReleaseVersion);
        Assert.Equal(new[] { "0.9.12", "0.9.11" }, session.Entries.Select(entry => entry.Version.ToString()));
    }

    [Fact]
    public void Manual_open_shows_complete_installed_history_after_dismissing_update()
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = "0.9.11";
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 12, 0));
        session.MarkPresented();
        session.Dismiss();
        session.Open();
        Assert.True(session.IsOpen);
        Assert.True(session.ShowingHistory);
        Assert.Contains(session.Entries, entry => entry.Version == new Version(0, 9, 0));
        Assert.Equal(new Version(0, 9, 12), session.Entries[0].Version);
        Assert.DoesNotContain(session.Entries, entry => entry.Version > new Version(0, 9, 12));
        session.MarkPresented();
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Downgrade_history_excludes_future_notes_and_preserves_newer_seen_version()
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = "0.9.13.0";
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 11));
        Assert.False(session.IsOpen);
        session.Open();
        Assert.Equal(new Version(0, 9, 11), session.Entries[0].Version);
        session.MarkPresented();
        Assert.Equal("0.9.13.0", store.Config.LastSeenReleaseVersion);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public void First_install_can_read_all_bundled_releases_through_installed_version(string? seen)
    {
        var store = new MemoryStore();
        store.Config.LastSeenReleaseVersion = seen;
        var session = new ReleaseNotesSession(new ConfigurationService(store), new(0, 9, 12));
        Assert.True(session.IsOpen);
        Assert.Equal(new Version(0, 9, 12), session.Entries[0].Version);
        Assert.Equal(new Version(0, 9, 0), session.Entries[^1].Version);
        Assert.Equal(0, store.Saves);
    }

    private sealed class MemoryStore : IConfigurationPersistence
    {
        public PoserConfiguration Config { get; } = new();
        public int Saves;
        public bool Fail;
        public ConfigurationLoadResult Load() => new(Config);
        public void Save(PoserConfiguration configuration)
        {
            if (Fail) throw new IOException("Storage unavailable");
            Saves++;
        }
    }
}
