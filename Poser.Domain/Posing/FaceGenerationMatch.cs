namespace Poser.Domain.Posing;

/// <summary>Face compatibility between a pose file and its target rig.</summary>
public enum FaceGenerationMatch
{
    Same,
    PreDawntrailFileOnDawntrailSkeleton,
    DawntrailFileOnOlderSkeleton,
}
