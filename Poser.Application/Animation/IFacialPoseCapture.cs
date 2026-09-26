using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Domain.Scene;

namespace Poser.Application.Animation;

/// <summary>Captures a facial timeline into the pose.</summary>
public interface IFacialPoseCapture
{
    bool IsPending { get; }
    GestureResult Begin(ActorId actor, ActorDescriptor descriptor);
}
