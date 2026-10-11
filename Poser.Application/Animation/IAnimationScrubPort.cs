using System.Collections.Generic;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Havok control enumeration and local-time writes for scrubbing.</summary>
public interface IAnimationScrubPort
{
    /// <summary>Every currently valid Havok control, freshly enumerated.
    /// The returned <c>SkeletonToken</c> on the reading identifies the
    /// enumeration; writing with a stale token is refused.</summary>
    IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token);

    /// <summary>
    /// Finds a slot control by its native slot index across skeleton partials.
    /// Null means the slot is empty or has no matching control. Base and Upper
    /// Body have verified indexes; other logical layers have no stable mapping.
    /// </summary>
    ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot, out ulong token);

    /// <summary>Writes a control's local time. Fails when the actor,
    /// skeleton, or control no longer matches <paramref name="token"/>,
    /// so a scrub can never land on a replaced skeleton.</summary>
    Outcome SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token);
}
