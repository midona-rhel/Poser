using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

public interface ILightFiles
{
    SceneCreationResult Import(string path, ObjectPlacementMode mode);
    SceneActionResult Export(LightId light, string path);
}
