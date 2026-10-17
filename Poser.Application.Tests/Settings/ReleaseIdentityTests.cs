using System.Text.Json;
using System.Text.RegularExpressions;
using Poser.Application.Settings;

namespace Poser.Application.Tests.Settings;

/// <summary>
/// One release, one identity: the assembly version, both manifests, the download
/// links, the in-app release notes and the changelog name the same release and
/// carry the same notes. A release that bumps one and forgets another fails here
/// instead of shipping notes for the wrong version, or none.
/// </summary>
public sealed class ReleaseIdentityTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "repo.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Versions_links_and_notes_name_the_same_release()
    {
        string root = Root();
        string csproj = File.ReadAllText(Path.Combine(root, "Poser", "Poser.csproj"));
        var version = Version.Parse(Regex.Match(csproj, @"<Version>([0-9.]+)</Version>").Groups[1].Value);
        string short3 = $"{version.Major}.{version.Minor}.{version.Build}";

        using var plugin = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Poser", "Poser.json")));
        using var repo = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "repo.json")));
        var listing = repo.RootElement[0];
        Assert.Equal(version.ToString(), plugin.RootElement.GetProperty("AssemblyVersion").GetString());
        Assert.Equal(version.ToString(), listing.GetProperty("AssemblyVersion").GetString());
        foreach (var link in new[] { "DownloadLinkInstall", "DownloadLinkTesting", "DownloadLinkUpdate" })
            Assert.Contains($"/download/v{short3}-beta/", listing.GetProperty(link).GetString());

        // The newest in-app entry is this release, and every manifest and the
        // changelog's newest section carry its bullets.
        var newest = ReleaseNotesHistory.Entries[0];
        Assert.Equal(short3, newest.Version.ToString(3));
        string bullets = string.Join("\n", newest.Highlights.Select(h => $"- {h}"));
        Assert.Equal(bullets, plugin.RootElement.GetProperty("Changelog").GetString());
        Assert.Equal(bullets, listing.GetProperty("Changelog").GetString());

        string changelog = File.ReadAllText(Path.Combine(root, "docs", "release", "CHANGELOG.md")).Replace("\r\n", "\n");
        var section = Regex.Match(changelog, @"^## (\S+)-beta — (.+?)\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.Equal(short3, section.Groups[1].Value);
        Assert.Equal(newest.Title, section.Groups[2].Value.Trim());
        Assert.Equal(bullets, string.Join("\n", section.Groups[3].Value.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal))));
    }
}
