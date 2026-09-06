using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Game.Integration;
using Xunit;

namespace Poser.Game.Tests.Integration;

public sealed class CharaRequestTests
{
    private static JObject Snapshot()
    {
        var customize = new JObject { ["ModelId"] = 0 };
        foreach (var key in Enum.GetValues<CustomizeKey>())
            customize[key.ToString()] = new JObject
            {
                ["Value"] = key == CustomizeKey.Wetness ? new JValue(false) : new JValue(key == CustomizeKey.Gender ? 0 : 1),
                ["Apply"] = true,
            };
        return new JObject
        {
            ["FileVersion"] = 1, ["Customize"] = customize,
            ["Equipment"] = new JObject(), ["Bonus"] = new JObject(),
            ["Parameters"] = JObject.Parse("""{"SkinDiffuse":{"Red":0.3,"Apply":true}}"""),
        };
    }

    [Fact]
    public void Named_body_and_packed_flags_match_native_mapping()
    {
        var result = CharaRequest.Build(Snapshot(), JObject.Parse("""
            {"Race":"AuRa","Tribe":"Xaela","Gender":"Feminine","Age":"Normal",
             "Hair":204,"Skintone":191,"EnableHighlights":true,"Eyes":133,"Mouth":131,
             "FacePaint":129,"FacialFeatures":"First, Seventh, LegacyTattoo"}
            """));
        Assert.True(result.Success, result.Detail);
        var c = result.Value!["Customize"]!;
        Assert.Equal(6, c["Race"]!["Value"]);
        Assert.Equal(12, c["Clan"]!["Value"]);
        Assert.Equal(204, c["Hairstyle"]!["Value"]);
        Assert.Equal(191, c["SkinColor"]!["Value"]);
        Assert.Equal(5, c["EyeShape"]!["Value"]);
        Assert.Equal(128, c["Highlights"]!["Value"]);
        Assert.Equal(128, c["SmallIris"]!["Value"]);
        Assert.Equal(3, c["Mouth"]!["Value"]);
        Assert.Equal(128, c["Lipstick"]!["Value"]);
        Assert.Equal(128, c["FacePaintReversed"]!["Value"]);
        Assert.Equal(64, c["FacialFeature7"]!["Value"]);
        Assert.Equal(0, c["FacialFeature2"]!["Value"]);
        Assert.Equal(128, c["LegacyTattoo"]!["Value"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(300)]
    public void Legacy_and_object_facewear_produce_identical_requests(int id)
    {
        var legacy = CharaRequest.Build(Snapshot(), new JObject { ["Glasses"] = id });
        var current = CharaRequest.Build(Snapshot(), new JObject { ["Glasses"] = new JObject { ["GlassesId"] = id } });
        Assert.True(legacy.Success, legacy.Detail);
        Assert.True(current.Success, current.Detail);
        Assert.True(JToken.DeepEquals(legacy.Value, current.Value));
    }

    [Fact]
    public void Viera_fixture_flags_preserve_each_native_bit()
    {
        var result = CharaRequest.Build(Snapshot(), JObject.Parse("""
            {"Race":"Viera","Tribe":"Rava","Gender":"Masculine","Hair":17,
             "Eyes":131,"Mouth":128,"FacialFeatures":"Third, Fifth, Sixth","Glasses":0}
            """));
        Assert.True(result.Success, result.Detail);
        var c = result.Value!["Customize"]!;
        Assert.Equal(3, c["EyeShape"]!["Value"]);
        Assert.Equal(128, c["SmallIris"]!["Value"]);
        Assert.Equal(128, c["Lipstick"]!["Value"]);
        Assert.Equal(4, c["FacialFeature3"]!["Value"]);
        Assert.Equal(16, c["FacialFeature5"]!["Value"]);
        Assert.Equal(32, c["FacialFeature6"]!["Value"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Glasses\":\"broken\"}")]
    [InlineData("{\"Glasses\":{}}")]
    [InlineData("{\"Glasses\":65536}")]
    [InlineData("{\"Hair\":256}")]
    [InlineData("{\"MainHand\":{\"ModelSet\":1}}")]
    [InlineData("{\"ModelType\":123,\"Hair\":1}")]
    [InlineData("{\"Race\":6,\"Tribe\":1}")]
    public void Preflight_refuses_bad_files_before_an_actor_is_needed(string json)
    {
        Assert.False(CharaRequest.Build(null, JObject.Parse(json)).Success);
    }

    [Fact]
    public void Cleared_flags_are_zero()
    {
        var result = CharaRequest.Build(Snapshot(), JObject.Parse("""
            {"Eyes":3,"Mouth":1,"FacePaint":2,"EnableHighlights":false,"FacialFeatures":"None"}
            """));
        Assert.True(result.Success, result.Detail);
        foreach (var key in new[] { "SmallIris", "Lipstick", "FacePaintReversed", "Highlights", "FacialFeature3", "LegacyTattoo" })
            Assert.Equal(0, result.Value!["Customize"]![key]!["Value"]);
    }

    [Fact]
    public void Gear_preserves_weapon_triple_both_dyes_rings_and_facewear()
    {
        var result = CharaRequest.Build(Snapshot(), JObject.Parse("""
            {"MainHand":{"ModelSet":301,"ModelBase":7,"ModelVariant":2,"DyeId":5,"DyeId2":9},
             "LeftRing":{"ModelBase":44,"ModelVariant":1},"RightRing":{"ModelBase":45,"ModelVariant":2},
             "HeadGear":{"ModelBase":0,"ModelVariant":0},"Glasses":{"GlassesId":12}}
            """));
        Assert.True(result.Success, result.Detail);
        var e = result.Value!["Equipment"]!;
        Assert.Equal(WardrobeIds.Custom(301, 7, 2), e["MainHand"]!["ItemId"]!.Value<ulong>());
        Assert.Equal(5, e["MainHand"]!["Stain"]);
        Assert.Equal(9, e["MainHand"]!["Stain2"]);
        Assert.Equal(WardrobeIds.Custom(44, 0, 1), e["LFinger"]!["ItemId"]!.Value<ulong>());
        Assert.Equal(WardrobeIds.Custom(45, 0, 2), e["RFinger"]!["ItemId"]!.Value<ulong>());
        Assert.Equal(0ul, e["Head"]!["ItemId"]!.Value<ulong>());
        Assert.Equal(12ul | (1ul << 49), result.Value["Bonus"]!["Glasses"]!["BonusId"]!.Value<ulong>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Hair\":256}")]
    [InlineData("{\"Hair\":-1}")]
    [InlineData("{\"Hair\":1.5}")]
    [InlineData("{\"Race\":\"Unknown\"}")]
    [InlineData("{\"Race\":6,\"Tribe\":1}")]
    [InlineData("{\"MainHand\":{\"ModelSet\":1}}")]
    public void Malformed_appearance_refuses_without_changing_snapshot(string json)
    {
        var snapshot = Snapshot();
        var before = snapshot.DeepClone();
        Assert.False(CharaRequest.Build(snapshot, JObject.Parse(json)).Success);
        Assert.True(JToken.DeepEquals(before, snapshot));
    }

    [Fact]
    public void Partial_file_keeps_omitted_fields_and_does_not_apply_unset_parameters()
    {
        var snapshot = Snapshot();
        var before = snapshot.DeepClone();
        var result = CharaRequest.Build(snapshot, JObject.Parse("""{"Hair":191,"Skintone":null}"""));
        Assert.True(result.Success, result.Detail);
        Assert.True(JToken.DeepEquals(before, snapshot));
        Assert.False(result.Value!["Customize"]!["SkinColor"]!["Apply"]!.Value<bool>());
        Assert.False(result.Value["Parameters"]!["SkinDiffuse"]!["Apply"]!.Value<bool>());
    }
}
