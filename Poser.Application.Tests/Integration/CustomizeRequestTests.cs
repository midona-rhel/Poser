using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Documents.Appearance;

namespace Poser.Application.Tests.Integration;

public sealed class CustomizeRequestTests
{
    [Fact]
    public void Parameter_copy_preserves_custom_decal_without_applying_other_appearance()
    {
        var snapshot = Snapshot();
        snapshot["Parameters"]!["DecalColor"] = JObject.Parse("""{"Red":0.16,"Green":0.05,"Blue":0.002,"Alpha":0.47,"Apply":true}""");
        var before = snapshot.DeepClone();
        var result = CustomizeRequest.ParametersOnly(snapshot);
        Assert.True(result.Success, result.Detail);
        var request = result.Value!;
        Assert.True(JToken.DeepEquals(snapshot["Parameters"], request["Parameters"]));
        foreach (var section in new[] { "Customize", "Equipment", "Bonus" })
            Assert.All(((JObject)request[section]!).Descendants().OfType<JProperty>()
                .Where(p => p.Name.StartsWith("Apply")), p => Assert.False(p.Value.Value<bool>()));
        Assert.Null(request["Materials"]);
        Assert.Null(request["Links"]);
        Assert.True(JToken.DeepEquals(before, snapshot));
    }

    private static JObject Snapshot()
    {
        var customize = new JObject();
        foreach (var key in Enum.GetValues<CustomizeKey>())
            customize[key.ToString()] = new JObject
            {
                ["Value"] = key == CustomizeKey.Wetness ? new JValue(false) : new JValue(1),
                ["Apply"] = true,
            };
        customize["Gender"]!["Value"] = 0;
        customize["Face"]!["Value"] = 7;
        return new JObject
        {
            ["FileVersion"] = 1,
            ["Customize"] = customize,
            ["Equipment"] = JObject.Parse("""{"Head":{"ItemId":123,"Apply":true,"ApplyStain":true,"ApplyCrest":true},"Hat":{"Show":false,"Apply":true}}"""),
            ["Bonus"] = JObject.Parse("""{"Glasses":{"BonusId":23,"Apply":true}}"""),
            ["Parameters"] = JObject.Parse("""{"SkinDiffuse":{"Red":0.2,"Green":0.3,"Blue":0.4,"Apply":true},"HairDiffuse":{"Red":0.7,"Apply":true}}"""),
            ["Materials"] = JObject.Parse("""{"00000001":{"Enabled":true,"DiffuseR":0.9}}"""),
            ["Links"] = new JArray("unrelated-design"),
        };
    }

    [Fact]
    public void Palette_request_has_only_requested_and_required_application()
    {
        const CustomizeKey key = CustomizeKey.SkinColor;
        const int value = 8;
        var snapshot = Snapshot();
        var before = snapshot.DeepClone();
        var result = CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [key] = value });
        Assert.True(result.Success, result.Detail);
        var request = result.Value!;
        Assert.True(JToken.DeepEquals(before, snapshot));
        Assert.Null(request["Materials"]);
        Assert.Null(request["Links"]);
        foreach (var section in new[] { "Equipment", "Bonus", "Parameters" })
            Assert.All(((JObject)request[section]!).Descendants().OfType<JProperty>()
                .Where(p => p.Name.StartsWith("Apply")), p => Assert.False(p.Value.Value<bool>()));
        Assert.Equal(0.2, request["Parameters"]!["SkinDiffuse"]!["Red"]!.Value<double>());
        Assert.Equal(123, request["Equipment"]!["Head"]!["ItemId"]!.Value<int>());
        foreach (var other in Enum.GetValues<CustomizeKey>())
        {
            Assert.Equal(other == key || other == CustomizeKey.BodyType,
                request["Customize"]![other.ToString()]!["Apply"]!.Value<bool>());
            if (other != key)
                Assert.True(JToken.DeepEquals(snapshot["Customize"]![other.ToString()]!["Value"], request["Customize"]![other.ToString()]!["Value"]));
        }
        Assert.Equal(value, request["Customize"]![key.ToString()]!["Value"]!.Value<int>());
    }

    [Fact]
    public void Body_changes_keep_parser_values_and_apply_the_structural_group()
    {
        var result = CustomizeRequest.Build(Snapshot(), new Dictionary<CustomizeKey, int>
        {
            [CustomizeKey.Race] = 6, [CustomizeKey.Clan] = 12, [CustomizeKey.Gender] = 1,
        });
        Assert.True(result.Success, result.Detail);
        var customize = result.Value!["Customize"]!;
        foreach (string key in new[] { "Race", "Clan", "Gender", "BodyType" })
            Assert.True(customize[key]!["Apply"]!.Value<bool>());
        Assert.Equal(7, customize["Face"]!["Value"]!.Value<int>());
        Assert.False(customize["Face"]!["Apply"]!.Value<bool>());
        Assert.Equal(12, customize["Clan"]!["Value"]!.Value<int>());
    }

    [Fact]
    public void Clan_implies_matching_race_and_gender_only_does_not_apply_clan()
    {
        var clan = CustomizeRequest.Build(Snapshot(), new Dictionary<CustomizeKey, int> { [CustomizeKey.Clan] = 2 });
        Assert.True(clan.Success);
        Assert.True(clan.Value!["Customize"]!["Race"]!["Apply"]!.Value<bool>());
        var gender = CustomizeRequest.Build(Snapshot(), new Dictionary<CustomizeKey, int> { [CustomizeKey.Gender] = 1 });
        Assert.True(gender.Success);
        Assert.False(gender.Value!["Customize"]!["Clan"]!["Apply"]!.Value<bool>());
    }

    [Theory]
    [InlineData("{\"Value\":\"8\"}", CustomizeKey.SkinColor, 8)]
    [InlineData(null, CustomizeKey.Race, 6)]
    public void Bad_request_is_refused_without_mutation(string? field, CustomizeKey key, int value)
    {
        // One malformed snapshot field, and one body that is inconsistent with its clan.
        var snapshot = Snapshot();
        if (field != null)
            snapshot["Customize"]!["SkinColor"] = JToken.Parse(field);
        var before = snapshot.DeepClone();
        var result = CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [key] = value });
        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.True(JToken.DeepEquals(before, snapshot));
    }
}
