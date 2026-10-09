using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Game.Integration;

namespace Poser.Game.Tests;

public sealed class SpawnAppearanceCopyTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void Copy_reads_owned_source_without_unlocking_or_keying_target(bool held, int success)
    {
        var source = JObject.Parse("{ 'Customize': { 'ModelId': 4962 } }");
        var before = source.ToString();
        var keys = new List<uint>();
        int writes = 0;
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (index, key) =>
            {
                Assert.Equal(17, index);
                keys.Add(key);
                return held && key == 0 ? (6, null) : (success, source);
            },
            () => IntegrationPortResult.Ok(),
            (state, index, key, flags) =>
            {
                writes++;
                Assert.Equal(18, index);
                Assert.Equal(0u, key);
                Assert.Equal(7ul, flags);
                var copy = Assert.IsType<JObject>(state);
                Assert.NotSame(source, copy);
                Assert.True(JToken.DeepEquals(source, copy));
                copy["Customize"]!["ModelId"] = 0;
                return success;
            });
        Assert.True(result.Success);
        Assert.Equal(1, writes);
        Assert.Equal(held ? new uint[] { 0, 0x504F5352 } : new uint[] { 0 }, keys);
        Assert.Equal(before, source.ToString());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(0)]
    public void Refused_or_missing_source_never_writes_target(int finalCode)
    {
        int reads = 0;
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, key) => { reads++; return (key == 0 ? 6 : finalCode, null); },
            () => throw new InvalidOperationException("Must not reset a target with a refused source."),
            (_, _, _, _) => throw new InvalidOperationException("Must not write a refused source."));
        Assert.False(result.Success);
        Assert.Equal(2, reads);
        if (finalCode == 6)
            Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }

    [Fact]
    public void Foreign_target_hold_is_not_retried_with_owner_key()
    {
        int writes = 0;
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, _) => (0, new JObject()),
            () => IntegrationPortResult.Ok(),
            (_, _, key, _) => { writes++; Assert.Equal(0u, key); return 6; });
        Assert.False(result.Success);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void Refused_target_initialization_never_applies_the_copy()
    {
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, _) => (0, new JObject()),
            () => IntegrationPortResult.Refused(GlamourerAccess.ForeignHeld),
            (_, _, _, _) => throw new InvalidOperationException("Must not apply to a refused target."));
        Assert.False(result.Success);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }

    [Fact]
    public void Target_initialization_precedes_source_application()
    {
        var calls = new List<string>();
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, _) => { calls.Add("read source"); return (0, new JObject()); },
            () => { calls.Add("initialize target"); return IntegrationPortResult.Ok(); },
            (_, _, _, _) => { calls.Add("apply copy"); return 0; });
        Assert.True(result.Success);
        Assert.Equal(new[] { "read source", "initialize target", "apply copy" }, calls);
    }
}
