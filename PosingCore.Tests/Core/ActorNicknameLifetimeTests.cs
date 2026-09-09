using System;
using Dalamud.Plugin;
using NSubstitute;
using Poser.Config;
using Poser.Domain.Identity;

namespace Poser.Tests.Core;

public sealed class ActorNicknameLifetimeTests
{
    [Fact]
    public void NicknameSurvivesSameSessionRebindingButNotNextSessionSlotReuse()
    {
        var plugin = Substitute.For<IDalamudPluginInterface>();
        plugin.GetPluginConfig().Returns(new PoserConfiguration());
        var names = new ConfigurationService(plugin);
        var original = new ActorId(Guid.NewGuid(), 0);
        var restored = new ActorId(original.LogicalId, 1);
        names.SetNickname(original.LogicalId, "My duplicate");
        Assert.Equal("My duplicate", names.GetDisplayName(restored.LogicalId, "Native (202)"));

        names.ResetSessionNames();
        names.ResetSessionNames();
        var unrelated = new ActorId(original.LogicalId, 2);
        Assert.Null(names.GetNickname(unrelated.LogicalId));
        Assert.Equal("Different player", names.GetDisplayName(unrelated.LogicalId, "Different player (202)"));
        names.SetNickname(unrelated.LogicalId, "New nickname");
        Assert.Equal("New nickname", names.GetDisplayName(unrelated.LogicalId, "Different player (202)"));
    }
}
