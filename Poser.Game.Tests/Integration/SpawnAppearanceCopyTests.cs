using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Game.Integration;

namespace Poser.Game.Tests;

public sealed class SpawnAppearanceCopyTests
{
    [Fact]
    public void Copy_reads_owned_source_without_unlocking_or_keying_target()
    {
        const int success = 0;
        var source = JObject.Parse("{ 'Customize': { 'ModelId': 4962 } }");
        var before = source.ToString();
        var keys = new List<uint>();
        int writes = 0;
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (index, key) =>
            {
                Assert.Equal(17, index);
                keys.Add(key);
                return key == 0 ? (6, null) : (success, source);
            },
            () => IntegrationResult.Ok(),
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
        Assert.Equal(new uint[] { 0, 0x504F5352 }, keys);
        Assert.Equal(before, source.ToString());
    }

    [Fact]
    public void Refused_source_never_writes_target()
    {
        int reads = 0;
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, key) => { reads++; return (6, null); },
            () => throw new InvalidOperationException("Must not reset a target with a refused source."),
            (_, _, _, _) => throw new InvalidOperationException("Must not write a refused source."));
        Assert.False(result.Success);
        Assert.Equal(2, reads);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }

    [Fact]
    public void Target_initialization_precedes_source_application()
    {
        var calls = new List<string>();
        var result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, _) => { calls.Add("read source"); return (0, new JObject()); },
            () => { calls.Add("initialize target"); return IntegrationResult.Ok(); },
            (_, _, _, _) => { calls.Add("apply copy"); return 0; });
        Assert.True(result.Success);
        Assert.Equal(new[] { "read source", "initialize target", "apply copy" }, calls);

        // A refused target initialization never applies the copy.
        result = IntegrationRuntimePort.CopySpawnAppearance(17, 18,
            (_, _) => (0, new JObject()),
            () => IntegrationResult.Refused(GlamourerAccess.ForeignHeld),
            (_, _, _, _) => throw new InvalidOperationException("Must not apply to a refused target."));
        Assert.False(result.Success);
        Assert.Equal(GlamourerAccessKind.ForeignHeld, result.AppearanceRefusal);
    }
}
