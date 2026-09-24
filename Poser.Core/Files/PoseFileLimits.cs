namespace Poser.Files;

/// <summary>Hard bounds for the ordinary Brio-compatible <c>.pose</c> codec.</summary>
public static class PoseFileLimits
{
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxEntriesPerCollection = 8_192;
    public const int MaxTotalEntries = 32_768;
    public const int MaxBoneNameCharacters = 256;
    public const int MaxTags = 256;
    public const int MaxTagCharacters = 256;
    public const float MinQuaternionLengthSquared = 0.000001f;
}
