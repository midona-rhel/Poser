using System;
using System.Numerics;
using Poser.Core;
using Xunit;

namespace Poser.Tests.Core;

public class TransformTests
{
    [Fact]
    public void Identity_HasCorrectValues()
    {
        // Arrange & Act
        var transform = Transform.Identity;

        // Assert
        Assert.Equal(Vector3.Zero, transform.Position);
        Assert.Equal(Quaternion.Identity, transform.Rotation);
        Assert.Equal(Vector3.One, transform.Scale);
    }

    [Fact]
    public void Constructor_DefaultValues()
    {
        // Arrange & Act
        var transform = new Transform();

        // Assert
        Assert.Equal(Vector3.Zero, transform.Position);
        Assert.Equal(Quaternion.Identity, transform.Rotation);
        Assert.Equal(Vector3.One, transform.Scale);
    }

    [Fact]
    public void Position_CanBeSet()
    {
        // Arrange
        var transform = new Transform();
        var expectedPosition = new Vector3(1, 2, 3);

        // Act
        transform.Position = expectedPosition;

        // Assert
        Assert.Equal(expectedPosition, transform.Position);
    }

    [Fact]
    public void Rotation_CanBeSet()
    {
        // Arrange
        var transform = new Transform();
        var expectedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4);

        // Act
        transform.Rotation = expectedRotation;

        // Assert
        Assert.Equal(expectedRotation, transform.Rotation);
    }

    [Fact]
    public void Scale_CanBeSet()
    {
        // Arrange
        var transform = new Transform();
        var expectedScale = new Vector3(2, 2, 2);

        // Act
        transform.Scale = expectedScale;

        // Assert
        Assert.Equal(expectedScale, transform.Scale);
    }

    [Fact]
    public void ToMatrix_Identity_ReturnsIdentityMatrix()
    {
        // Arrange
        var transform = Transform.Identity;

        // Act
        var matrix = transform.ToMatrix();

        // Assert
        Assert.Equal(Matrix4x4.Identity, matrix);
    }

    [Fact]
    public void ToMatrix_WithPosition_ReturnsCorrectMatrix()
    {
        // Arrange
        var transform = Transform.Identity;
        transform.Position = new Vector3(10, 20, 30);

        // Act
        var matrix = transform.ToMatrix();

        // Assert
        Assert.Equal(new Vector3(10, 20, 30), matrix.Translation);
    }

    [Fact]
    public void FromMatrix_ExtractsCorrectTransform()
    {
        // Arrange
        var originalTransform = Transform.Identity;
        originalTransform.Position = new Vector3(5, 10, 15);
        originalTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        originalTransform.Scale = new Vector3(2, 2, 2);
        var matrix = originalTransform.ToMatrix();

        // Act
        var extractedTransform = Transform.FromMatrix(matrix);

        // Assert
        Assert.Equal(originalTransform.Position.X, extractedTransform.Position.X, 4);
        Assert.Equal(originalTransform.Position.Y, extractedTransform.Position.Y, 4);
        Assert.Equal(originalTransform.Position.Z, extractedTransform.Position.Z, 4);
    }
}
