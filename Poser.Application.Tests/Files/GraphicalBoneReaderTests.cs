using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Poser.Data;

namespace Poser.Tests.Files;

public sealed class GraphicalBoneReaderTests
{
    [Fact]
    public void Embedded_config_is_read_once_and_shared()
    {
        var first = GraphicalBoneReader.ReadEmbeddedResource();
        Assert.Same(first, GraphicalBoneReader.ReadEmbeddedResource());
        Assert.NotEmpty(first.PoseImages);
    }

    [Fact]
    public void A_section_inherits_its_parent_bones_and_parsed_positions()
    {
        const string json = """
            {"poseImages":{
              "Body":{"image":"PoseBody","bones":[{"name":"j_kosi","position":"10, 20"}]},
              "Hand":{"image":"PoseHand","parent":"Body","bones":[{"name":"j_te_l","position":"3,4"}]}}}
            """;
        var config = GraphicalBoneReader.ReadStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var hand = config.PoseImages["Hand"].Bones;
        Assert.Equal(new[] { "j_te_l", "j_kosi" }, hand.Select(bone => bone.Name));
        Assert.Equal(new Vector2(10, 20), hand[1].PositionVector);
        Assert.Single(config.PoseImages["Body"].Bones);
    }
}
