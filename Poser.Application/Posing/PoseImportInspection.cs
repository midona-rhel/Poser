using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Detached compatibility facts; reading these does not apply or modify a pose.</summary>
public sealed record PoseImportInspection(
    bool IsExpressionOnly,
    bool IsBodyOnly,
    bool CanImportExpression,
    FaceGenerationMatch FaceGeneration);
