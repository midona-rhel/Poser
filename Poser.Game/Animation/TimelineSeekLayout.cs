namespace Poser.Game.Animation;

/// <summary>Scheduler clip classes the seek distinguishes (#533).</summary>
internal enum SeekClipKind
{
    Other,
    /// <summary>C010: runs a child timeline at clip+0x138.</summary>
    ChildTimeline,
    /// <summary>C009: drives one Havok control (clip+0x98 instance).</summary>
    HavokAnimation,
    /// <summary>C012: spawns a VFX object.</summary>
    Vfx,
    /// <summary>C173 async VFX: up to 8 instances in an inline array (clip+0x98,
    /// pointer at +0xD8, count clamp(data+0x18, 1, 8)); ignores timeline pause/speed.</summary>
    AsyncVfx,
    /// <summary>C031 "Summon Animation": plays an ActionTimeline on a weapon
    /// object's own bucket-3 timeline.</summary>
    SummonAnimation,
    /// <summary>C198: requests a prop model on the actor control (type 8 cell of
    /// its group, model id data+0x18, group data+0x14).</summary>
    WeaponRequest,
    /// <summary>C043 Summon Weapon: a request of type data+0xC on group 0; type 8
    /// asks for the prop model data+0x10, like a C198.</summary>
    SummonWeapon,
    /// <summary>C094 Invisibility: an alpha fade on the body and DrawData weapon
    /// slots by its mask clip+0xD3 (bit 0 body, bits 1-3 slots 0-2).</summary>
    Invisibility,
}

/// <summary>
/// Build-specific identities the seek needs beyond function signatures: clip
/// classification, the engine's default event payload and the two managers
/// it reads. Resolved once; <see cref="Ready"/> false disables the seek.
/// </summary>
internal interface ITimelineSeekLayout
{
    bool Ready { get; }
    string? Missing { get; }
    SeekClipKind Classify(nint clip);
    /// <summary>True when <paramref name="timeline"/> is a C010's child timeline.</summary>
    bool IsChildTimeline(nint timeline);
    /// <summary>True when <paramref name="control"/> is an actor control (the
    /// object that owns +0x6B8 and the C009 instance list at +0x30).</summary>
    bool IsActorControl(nint control);
    /// <summary>The engine's default event payload ({1, 0}).</summary>
    nint DefaultPayload { get; }
    /// <summary>The ActionTimelineManager instance (bucket-3 groups at +0x30).</summary>
    nint TimelineManager { get; }
    /// <summary>The Apricot job-system slot table base (entries at +0x2000).</summary>
    nint VfxSlotTable { get; }
    /// <summary>ScheduleManagement instance (frame delta in frames at +0x50, the
    /// ε the clip sync subtracts), or 0.</summary>
    nint ScheduleManagement { get; }
    /// <summary>Apricot instance release(handle, clearFlag).</summary>
    unsafe delegate* unmanaged<ulong, byte, void> ReleaseVfx { get; }
}
