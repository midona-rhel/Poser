using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>Initializes appearance for an actor Poser has just spawned.
/// Called before its first deferred draw; existing scene actors are not targets.</summary>
public interface ISpawnAppearancePort
{
    IntegrationResult ResetSpawnAppearance(nint address);
    IntegrationResult CopySpawnAppearance(nint sourceAddress, nint targetAddress);
}
