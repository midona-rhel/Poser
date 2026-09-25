using Poser.Domain.Identity;

namespace Poser.Application.Scene;

/// <summary>Camera file operations retain IDs and creation receipts, never native cameras.</summary>
public interface ICameraFiles
{
    SceneCreationResult Import(string path);
    SceneActionResult Export(CameraId camera, string path);
}
