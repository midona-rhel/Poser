using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;
using Poser.Documents.Appearance;
using Xunit;

namespace Poser.Application.Tests.Integration;

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

    [Fact]
    public void Legacy_and_object_facewear_produce_identical_requests()
    {
        const int id = 12;
        var legacy = CharaRequest.Build(Snapshot(), new JObject { ["Glasses"] = id });
        var current = CharaRequest.Build(Snapshot(), new JObject { ["Glasses"] = new JObject { ["GlassesId"] = id } });
        Assert.True(legacy.Success, legacy.Detail);
        Assert.True(current.Success, current.Detail);
        Assert.True(JToken.DeepEquals(legacy.Value, current.Value));
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
    [InlineData("{\"Race\":6,\"Tribe\":1}")]
    public void Bad_files_are_refused_without_changing_the_snapshot(string json)
    {
        Assert.False(CharaRequest.Build(null, JObject.Parse(json)).Success);
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
