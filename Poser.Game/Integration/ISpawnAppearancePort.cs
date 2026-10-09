using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>Appearance initialization for a newly owned, self-identified native spawn.
/// Called before its first deferred draw, never against a source actor or an existing scene actor.</summary>
public interface ISpawnAppearancePort
{
    IntegrationPortResult ResetSpawnAppearance(nint address);
}
