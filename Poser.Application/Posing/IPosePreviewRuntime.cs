using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Posing;

/// <summary>Native preview lifecycle and ordered application, separate from its rendered surface.</summary>
public interface IPosePreviewRuntime
{
    bool IsActive { get; }
    void Open(ActorId source);
    void ShowSequence(PosePreviewRequest first, PosePreviewRequest second);
    void Close();
}
