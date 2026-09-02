using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>One bone in a picker: the host's categorised bone list, shared
/// by the camera's tracking picker and IK's bone target.</summary>
public sealed record BoneChoice(
    string Key,
    string Label,
    string SearchText,
    BoneId BoneId,
    string? Badge);
