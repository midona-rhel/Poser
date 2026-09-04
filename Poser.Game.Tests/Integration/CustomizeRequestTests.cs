using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Game.Integration;

namespace Poser.Game.Tests.Integration;

public sealed class CustomizeRequestTests
{
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

    [Theory]
    [InlineData(CustomizeKey.SkinColor, 7)]
    [InlineData(CustomizeKey.SkinColor, 8)]
    [InlineData(CustomizeKey.HairColor, 207)]
    public void Palette_request_has_only_requested_and_required_application(CustomizeKey key, int value)
    {
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
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Value\":\"8\"}")]
    [InlineData("{\"Value\":8.5}")]
    [InlineData("{\"Value\":8,\"Apply\":\"yes\"}")]
    public void Missing_or_malformed_requested_field_is_refused_without_mutation(string field)
    {
        var snapshot = Snapshot();
        snapshot["Customize"]!["SkinColor"] = JToken.Parse(field);
        var before = snapshot.DeepClone();
        var result = CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [CustomizeKey.SkinColor] = 8 });
        Assert.False(result.Success);
        Assert.Contains("SkinColor", result.Detail);
        Assert.Null(result.Value);
        Assert.True(JToken.DeepEquals(before, snapshot));
    }

    [Fact]
    public void Omitted_false_apply_flag_is_valid_and_wetness_stays_boolean()
    {
        var snapshot = Snapshot();
        ((JObject)snapshot["Customize"]!["Wetness"]!).Remove("Apply");
        var result = CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [CustomizeKey.Wetness] = 1 });
        Assert.True(result.Success);
        Assert.Equal(JTokenType.Boolean, result.Value!["Customize"]!["Wetness"]!["Value"]!.Type);
        Assert.True(result.Value!["Customize"]!["Wetness"]!["Value"]!.Value<bool>());
    }

    [Theory]
    [InlineData(CustomizeKey.SkinColor, -1)]
    [InlineData(CustomizeKey.HairColor, 256)]
    [InlineData(CustomizeKey.Wetness, 2)]
    [InlineData(CustomizeKey.Race, 6)]
    [InlineData(CustomizeKey.Gender, 3)]
    public void Invalid_values_or_inconsistent_body_are_refused(CustomizeKey key, int value)
        => Assert.False(CustomizeRequest.Build(Snapshot(), new Dictionary<CustomizeKey, int> { [key] = value }).Success);

    [Fact]
    public void Structural_data_is_required_even_for_a_palette_edit()
    {
        var snapshot = Snapshot();
        ((JObject)snapshot["Customize"]!).Remove("Clan");
        var result = CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [CustomizeKey.SkinColor] = 8 });
        Assert.False(result.Success);
        Assert.Contains("Clan", result.Detail);
    }

    [Fact]
    public void Nonhuman_and_oversized_version_are_refused_without_throwing()
    {
        var snapshot = Snapshot();
        snapshot["Customize"]!["ModelId"] = 1000;
        Assert.False(CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [CustomizeKey.SkinColor] = 8 }).Success);
        snapshot["FileVersion"] = long.MaxValue;
        Assert.False(CustomizeRequest.Build(snapshot, new Dictionary<CustomizeKey, int> { [CustomizeKey.SkinColor] = 8 }).Success);
    }
}
