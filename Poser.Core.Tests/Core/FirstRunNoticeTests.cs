using System;
using Poser.Config;

namespace Poser.Tests.Core;

/// <summary>
/// The acceptance gate's two decisions. The typed phrase is forgiven its
/// surrounding whitespace and its casing and nothing else, and acceptance is
/// stored as a version so a revised notice re-prompts a config that accepted
/// the previous one.
/// </summary>
public class FirstRunNoticeTests
{
    [Theory]
    [InlineData("I accept")]
    [InlineData("i accept")]
    [InlineData("I ACCEPT ")]
    [InlineData("  I Accept\t")]
    public void ConfirmsAcceptsTrimmedCaseInsensitivePhrase(string typed) =>
        Assert.True(FirstRunNotice.Confirms(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Interior whitespace is NOT collapsed: the dialog quotes the phrase, so
    // the phrase is what it takes.
    [InlineData("I  accept")]
    [InlineData("Iaccept")]
    [InlineData("accept")]
    [InlineData("I accept.")]
    [InlineData("I accept the terms")]
    public void ConfirmsRejectsAnythingElse(string? typed) =>
        Assert.False(FirstRunNotice.Confirms(typed));

    [Fact]
    public void FreshConfigIsNotAccepted() =>
        Assert.False(FirstRunNotice.IsAccepted(new PoserConfiguration()));

    [Fact]
    public void AcceptRecordsThisBuildsNoticeVersion()
    {
        var config = new PoserConfiguration();

        FirstRunNotice.Accept(config);

        Assert.Equal(FirstRunNotice.CurrentVersion, config.AcceptedNoticeVersion);
        Assert.True(FirstRunNotice.IsAccepted(config));
    }

    [Fact]
    public void AConfigThatAcceptedAnOlderNoticeIsPromptedAgain()
    {
        var config = new PoserConfiguration
        {
            AcceptedNoticeVersion = FirstRunNotice.CurrentVersion - 1,
        };

        Assert.False(FirstRunNotice.IsAccepted(config));
    }

    [Fact]
    public void AConfigThatAcceptedALaterNoticeStaysAccepted()
    {
        // A downgrade must not re-prompt: the user accepted a notice that
        // said at least as much as this build's.
        var config = new PoserConfiguration
        {
            AcceptedNoticeVersion = FirstRunNotice.CurrentVersion + 1,
        };

        Assert.True(FirstRunNotice.IsAccepted(config));
    }

    [Fact]
    public void EveryCreditedProjectNamesARepositoryAndItsMaintainers()
    {
        Assert.Equal(3, FirstRunNotice.Upstream.Length);
        foreach (var project in FirstRunNotice.Upstream)
        {
            Assert.False(string.IsNullOrWhiteSpace(project.Name));
            Assert.False(string.IsNullOrWhiteSpace(project.Credit));
            Assert.StartsWith("https://github.com/", project.Url, StringComparison.Ordinal);
        }
    }
}
