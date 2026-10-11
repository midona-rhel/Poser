namespace Poser.Documents.Files;

/// <summary>The wire values of <see cref="SceneStructureRef.Kind"/>. They are
/// written into scene files, so a value never changes; Kind stays a string so
/// an unknown future kind reads and skips rather than failing the file.</summary>
public static class SceneStructureKind
{
    public const string Actor = "actor";
    public const string Companion = "companion";
    public const string Prop = "prop";
    public const string WorldObject = "worldObject";
    public const string Light = "light";
    public const string Camera = "camera";
    public const string Overlay = "overlay";
    public const string Group = "group";
}
