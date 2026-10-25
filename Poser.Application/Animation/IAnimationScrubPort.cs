using System.Collections.Generic;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Havok control enumeration and local-time writes for scrubbing.</summary>
public interface IAnimationScrubPort
{
    /// <summary>Reads the actor's animation controls and returns the skeleton
    /// version in token. Pass that token back when setting a control's time.</summary>
    IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token);

    /// <summary>
    /// Finds a slot control by its native slot index across skeleton partials.
    /// Null means the slot is empty or has no matching control. Base and Upper
    /// Body have verified indexes; other logical layers have no stable mapping.
    /// </summary>
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot, out ulong token);

    /// <summary>Sets the control's time. Implementations must reject a stale
    /// skeleton token before writing, since control indices can be reused.</summary>
    Outcome SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token);
}
