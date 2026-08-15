using System.Numerics;
using Poser.Files;

namespace Poser.Tests.Files;

/// <summary>
/// The pin is a THREE-state value on the wire — absent, or a point that may
/// legitimately be the world origin — so the camera file has to tell "not
/// pinned" from "pinned at 0, 0, 0". A sentinel would collapse them.
/// </summary>
public class CameraFileFixedPositionTests
{
    [Fact]
    public void AnUnpinnedCameraRoundTripsAsUnpinned()
    {
        var file = new CameraFile { Name = "Wide" };

        var loaded = CameraFile.FromJson(Json(file));

        Assert.NotNull(loaded);
        Assert.Null(loaded!.FixedPosition);
    }

    [Fact]
    public void APinnedCameraRoundTripsItsPoint()
    {
        var file = new CameraFile
        {
            Name = "Locked off",
            FixedPosition = new Vector3(12.5f, -3f, 40.25f),
        };

        var loaded = CameraFile.FromJson(Json(file));

        Assert.NotNull(loaded);
        Assert.Equal(new Vector3(12.5f, -3f, 40.25f), loaded!.FixedPosition);
    }

    [Fact]
    public void APinAtTheOriginIsStillAPin()
    {
        var file = new CameraFile { FixedPosition = Vector3.Zero };

        var loaded = CameraFile.FromJson(Json(file));

        Assert.NotNull(loaded);
        Assert.Equal(Vector3.Zero, loaded!.FixedPosition);
    }

    [Fact]
    public void AFileWrittenBeforeThePinExistedLoadsUnpinned()
    {
        var loaded = CameraFile.FromJson(
            """{ "TypeName": "Poser Camera", "FileVersion": 1, "Name": "Old" }""");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.FixedPosition);
    }

    private static string Json(CameraFile file)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "poser-camera-" + System.Guid.NewGuid().ToString("N") + ".posercam");
        Assert.True(file.Save(path));
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
