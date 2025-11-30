using Poser.Core;
using Xunit;

namespace Poser.Tests.Core;

public class EntityIdTests
{
    [Fact]
    public void Constructor_SetsUniqueValue()
    {
        // Arrange & Act
        var id = new EntityId("test_123");

        // Assert
        Assert.Equal("test_123", id.Unique);
    }

    [Fact]
    public void New_GeneratesUniqueId()
    {
        // Arrange & Act
        var id1 = EntityId.New();
        var id2 = EntityId.New();

        // Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ImplicitConversion_FromString()
    {
        // Arrange & Act
        EntityId id = "test_string";

        // Assert
        Assert.Equal("test_string", id.Unique);
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        // Arrange
        var id1 = new EntityId("same_value");
        var id2 = new EntityId("same_value");

        // Act & Assert
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Equality_DifferentValue_AreNotEqual()
    {
        // Arrange
        var id1 = new EntityId("value_1");
        var id2 = new EntityId("value_2");

        // Act & Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GetHashCode_SameValue_SameHash()
    {
        // Arrange
        var id1 = new EntityId("test");
        var id2 = new EntityId("test");

        // Act & Assert
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
    }
}
