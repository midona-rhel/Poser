using System;
using System.Collections.Generic;

namespace Poser.Game.Animation;

/// <summary>
/// Same-frame seek of one live slot timeline through the game's own scheduler
/// (#533). Forward is one Advance in place. Backward is a manual loop wrap: stop
/// the clips, rewind, re-activate frame 0 and Advance 0 → T, so every clip
/// enters and leaves exactly as in natural playback (props spawn, VFX pre-warm by
/// their local time, the weapon-request counts stay balanced). Proven live with
/// the probe ops (reverse-engineering notes/timeline-restore.md).
/// Framework thread only.
/// </summary>
internal sealed unsafe class TimelineSeekEngine
{
    private readonly TimelineSeekNatives _n;
    private readonly ITimelineSeekLayout _l;

    public TimelineSeekEngine(TimelineSeekNatives natives, ITimelineSeekLayout layout)
    {
        _n = natives;
        _l = layout;
    }

    public bool Ready => _n.Ready && _l.Ready;

    // ── Clip walk ─────────────────────────────────────────────────────
    // actors (tl+0x18, next +0x20) → tracks (actor+0x28, u16 count +0x32) →
    // clips (track+0x18, u16 count +0x22); only clips with data (+0x48).

    private static void ClipsOf(nint timeline, List<nint> into)
    {
        nint actor = *(nint*)(timeline + 0x18);
        for (int a = 0; actor != 0 && a < 32; a++, actor = *(nint*)(actor + 0x20))
        {
            nint tracks = *(nint*)(actor + 0x28);
            int trackCount = *(ushort*)(actor + 0x32);
            for (int t = 0; tracks != 0 && t < trackCount && t < 64; t++)
            {
                nint track = *(nint*)(tracks + t * 8);
                if (track == 0)
                    continue;
                nint clips = *(nint*)(track + 0x18);
                int clipCount = *(ushort*)(track + 0x22);
                for (int c = 0; clips != 0 && c < clipCount && c < 256; c++)
                {
                    nint clip = *(nint*)(clips + c * 8);
                    if (clip != 0 && *(nint*)(clip + 0x48) != 0)
                        into.Add(clip);
                }
            }
        }
    }

    /// <summary>A C010's child timeline, or 0.</summary>
    private nint ChildOf(nint c010)
    {
        nint child = *(nint*)(c010 + 0x138);
        return child != 0 && _l.IsChildTimeline(child) ? child : 0;
    }

    /// <summary>Everything one seek touches, collected from the slot timeline's tree.</summary>
    internal sealed class Tree
    {
        public readonly List<(nint Clip, nint Child)> C010s = new();
        public readonly Dictionary<nint, nint> Havok = new();          // C009 → owner timeline
        public readonly List<nint> Vfx = new();                         // C012
        public readonly List<(nint Clip, float Start)> Summons = new(); // C031 + absolute start
    }

    public Tree Collect(nint root)
    {
        var tree = new Tree();
        var seen = new HashSet<nint>();
        var clips = new List<nint>();
        void Walk(nint t, float baseStart, int depth)
        {
            if (depth > 4 || !seen.Add(t))
                return;
            clips.Clear();
            ClipsOf(t, clips);
            foreach (var clip in clips.ToArray())
            {
                switch (_l.Classify(clip))
                {
                    case SeekClipKind.ChildTimeline:
                        var child = ChildOf(clip);
                        if (child == 0)
                            break;
                        tree.C010s.Add((clip, child));
                        // A child's frame 0 is its C010's +0x50 on the parent clock.
                        Walk(child, baseStart + *(float*)(clip + 0x50), depth + 1);
                        break;
                    case SeekClipKind.HavokAnimation:
                        tree.Havok[clip] = t;
                        break;
                    case SeekClipKind.Vfx:
                        tree.Vfx.Add(clip);
                        break;
                    case SeekClipKind.SummonAnimation:
                        tree.Summons.Add((clip, baseStart + *(float*)(clip + 0x50)));
                        break;
                }
            }
        }
        Walk(root, 0f, 0);
        return tree;
    }

    // ── Clock ─────────────────────────────────────────────────────────

    /// <summary>A timeline's clock as the game reads it: integer mode +0x64,
    /// finished +0x48, otherwise +0x34.</summary>
    public static float Clock(nint timeline)
    {
        byte flags = *(byte*)(timeline + 0x7C);
        if ((flags & 0x20) != 0)
            return *(uint*)(timeline + 0x64);
        if ((flags & 0x40) != 0)
            return *(float*)(timeline + 0x48);
        return *(float*)(timeline + 0x34);
    }

    public static bool IsIntegerFrame(nint timeline) => (*(byte*)(timeline + 0x7C) & 0x20) != 0;
    public static bool IsLoaded(nint timeline) => *(int*)(timeline + 0x78) == 5;
    public static float Length(nint timeline) => *(float*)(timeline + 0x48);

    // ── Speed window ──────────────────────────────────────────────────
    // Advance multiplies dt by tl+0x240 and the clip sync multiplies the whole
    // elapsed by the ROOT speed, so both run at exactly 1.0. Poser's pause is
    // speed 0; the caller restores it. Never window a C010 child: its own +0x240
    // is an unused denormal, and SetSpeed(child, x) fires 0x1E(x) into the
    // child's C012s, freezing their VFX (the stuck bubble emitter).

    private float OpenWindow(nint root)
    {
        float speed = *(float*)(root + 0x240);
        if (speed != 1f)
            _n.SetSpeed(root, 1f);
        return speed;
    }

    private void CloseWindow(nint root, float speed)
    {
        if (*(float*)(root + 0x240) != speed)
            _n.SetSpeed(root, speed);
    }

    // ── Clip sync ─────────────────────────────────────────────────────
    // FUN_141C30E40 writes frames − ε with ε = ScheduleManagement+0x50, this
    // frame's dt in frames. Natural playback syncs INSIDE the scheduler step, and
    // the same frame's Havok step adds dt back; an explicit sync in
    // Framework.Update (before the tick) would leave the control ε behind for
    // good. ε is zeroed around each explicit sync only, never around
    // UpdateBanner (Advance's hold clamp reads it).

    private void SyncNoEpsilon(nint clip)
    {
        nint schedule = _l.ScheduleManagement;
        if (schedule == 0)
        {
            _n.SyncLocalTime(clip);
            *(byte*)(clip + 0xCC) &= 0xFE;
            return;
        }
        float epsilon = *(float*)(schedule + 0x50);
        *(float*)(schedule + 0x50) = 0f;
        try { _n.SyncLocalTime(clip); }
        finally { *(float*)(schedule + 0x50) = epsilon; }
        // +0xCC bit 0 = "post-start sync pending", read only by the C009 post-steps
        // (slots 51/52). A clip restarted inside the seek kept it (its own post-step
        // failed the speed-pending guard), so the next tick re-synced it at the
        // paused root speed 0, i.e. to its frame 0: the backward-scrub flash.
        *(byte*)(clip + 0xCC) &= 0xFE;
    }


    /// <summary>The game's own guard before a C009 sync: an instance, not
    /// finished (+0x34 bit 5), and past its first state.</summary>
    private static bool CanSync(nint c009)
    {
        nint instance = *(nint*)(c009 + 0x98);
        return instance != 0 && (*(uint*)(instance + 0x34) & 0x20) == 0 && *(uint*)(c009 + 0xB0) > 1;
    }

    private static bool CanSyncNow(nint c009)
    {
        nint instance = *(nint*)(c009 + 0x98);
        return instance != 0 && *(nint*)(instance + 0x78) != 0 && *(uint*)(c009 + 0xB0) > 1;
    }

    /// <summary>Syncs every guarded C009 inside a root speed window; returns the
    /// ones that are not ready yet (they activated inside the step).</summary>
    public int SyncHavok(nint root, IEnumerable<nint> clips, HashSet<nint>? pending, bool immediate = false)
    {
        float speed = OpenWindow(root);
        int synced = 0;
        foreach (var clip in clips)
        {
            // On the seek frame a just-started clip still has its speed-pending bit
            // (inst+0x34 & 0x20); that only defers its PlaybackSpeed restore, not
            // LocalTime, so the instance and a resolved length are enough.
            if (immediate ? !CanSyncNow(clip) : !CanSync(clip))
            {
                pending?.Add(clip);
                continue;
            }
            pending?.Remove(clip);
            SyncNoEpsilon(clip);
            synced++;
        }
        CloseWindow(root, speed);
        return synced;
    }

    // ── Seek ──────────────────────────────────────────────────────────

    /// <summary>
    /// Seeks <paramref name="root"/> (a live, loaded slot timeline) to
    /// <paramref name="target"/> frames and fills <paramref name="work"/> with the
    /// follow-up for the next frames. Returns null on success, else why not.
    /// </summary>
    public string? Seek(nint character, nint root, float target, TimelineSeekWork work)
    {
        if (!Ready)
            return "native seek unavailable";
        if (!IsLoaded(root))
            return "timeline still loading";
        if (IsIntegerFrame(root))
            return "integer-frame timeline";
        if ((*(byte*)(root + 0x7C) & 0x40) != 0)
            return "timeline already finished";
        // Any pause bit besides the game's own bit 2 freezes Advance: no step.
        if ((*(uint*)(root + 0x54) & ~4u) != 0)
            return "timeline is held by a pause bit";

        target = Math.Clamp(target, 0f, Math.Max(0f, Length(root) - 1f));
        var tree = Collect(root);
        work.Begin(character, root, VfxHandles(tree));

        if (target < Clock(root) - 0.001f)
            Wrap(root, tree);
        float speed = OpenWindow(root);

        nint control = _n.GetActorControl(root);
        if (control != 0 && _l.IsActorControl(control))
            *(byte*)(control + 0x6B8) = 1; // start the re-entered clips without a blend-in
        for (int i = 0; i < 400; i++)
        {
            float step = Math.Min(target - *(float*)(root + 0x34), Length(root));
            if (step <= 0.001f)
                break;
            _n.UpdateBanner(root, step, 1);
            if ((*(byte*)(root + 0x272) & 0x80) != 0)
                break;
        }

        // Re-collect: the step may have created clips' instances (C009s that just
        // started, C031 prop timelines, C012 VFX objects).
        tree = Collect(root);
        // C009s started inside the step have no length (+0xB0) until their first
        // post-step next frame; resolve it now so the new control is synced on
        // the seek frame itself.
        foreach (var clip in tree.Havok.Keys)
        {
            nint instance = *(nint*)(clip + 0x98);
            if (instance != 0 && *(nint*)(instance + 0x78) != 0 && (*(byte*)(clip + 0xCC) & 0x40) == 0)
                _n.ResolveHavokLength(clip);
        }
        SyncHavok(root, tree.Havok.Keys, work.PendingHavok, immediate: true);
        CloseWindow(root, speed);

        work.OwnerSpeed = speed;
        foreach (var (clip, start) in tree.Summons)
            work.Props.Add(new TimelineSeekWork.Prop(clip, start));
        work.PumpProps(this);
        // The window fired 0x1E(1) into the C012s and the restore to 0 was dropped.
        if (speed == 0f)
            work.FreezeVfx(this);
        return null;
    }

    /// <summary>
    /// The manual loop wrap (Mac): event 9 through each non-C010 clip's own
    /// ProcessEvent (vtbl slot 9), children first and deepest first; never to a
    /// C010 (its Stop marks the child finished and the parent ENDS). Then
    /// un-finish children of inactive finished C010s, rewind, and run the start's
    /// frame-0 activation (clips starting at 0 can't be crossed by the step).
    /// Do not resync active C010s (slot 31) afterwards: it double-starts C043.
    /// </summary>
    private void Wrap(nint root, Tree tree)
    {
        // Event 9 stops a C009 with reason 2, which fades its Havok control out
        // over root tl+0x220 frames: the OLD pose still renders at weight ~1 on
        // the seek frame (the one-frame flash). With 0 the holder gets Stop(0) and
        // the pool update releases it inside the coming tick, before the render.
        uint fade = *(uint*)(root + 0x220);
        *(uint*)(root + 0x220) = 0;
        try { StopAll(root); }
        finally { *(uint*)(root + 0x220) = fade; }
        Rewind(root, tree);
    }

    private void StopAll(nint root)
    {
        var visited = new HashSet<nint>();
        var clips = new List<nint>();
        void Reset(nint t, int depth)
        {
            if (depth > 4 || !visited.Add(t))
                return;
            var own = new List<nint>();
            ClipsOf(t, own);
            foreach (var clip in own)
                if (_l.Classify(clip) == SeekClipKind.ChildTimeline && ChildOf(clip) is var child and not 0)
                    Reset(child, depth + 1);
            foreach (var clip in own)
            {
                if (_l.Classify(clip) == SeekClipKind.ChildTimeline)
                    continue;
                var processEvent = (delegate* unmanaged<nint, int, nint, byte>)(*(nint*)(*(nint*)clip + 9 * 8));
                processEvent(clip, 9, _l.DefaultPayload);
            }
        }
        Reset(root, 0);
    }

    private void Rewind(nint root, Tree tree)
    {
        // An inactive C010 whose child already finished would never restart it;
        // the game has no path for this, so its finish/pass fields are cleared.
        foreach (var (c010, child) in tree.C010s)
        {
            if ((*(byte*)(c010 + 0x10) & 0x30) != 0 || (*(byte*)(child + 0x7C) & 0x40) == 0)
                continue;
            *(byte*)(child + 0x7C) &= 0xBF;
            *(uint*)(child + 0x74) = 0;
            *(uint*)(child + 0x58) = 0;
            *(uint*)(child + 0x5C) = 0;
            _n.SetCurrentTime(child, 0f);
        }

        _n.SetCurrentTime(root, 0f);
        _n.ActivateClips(root, 4);
        bool handled = _n.ProcessPending(root, 0x11) != 0;
        *(byte*)(root + 0x272) = (byte)((handled ? 0 : 0x80) | (*(byte*)(root + 0x272) & 0x7F));
        if (handled)
            _n.ProcessAll(root, 0x12, _l.DefaultPayload);
    }

    // ── Props (C031) ──────────────────────────────────────────────────

    /// <summary>The weapon object a C031 plays on: slot u16 data+0xE; 0–2 are the
    /// DrawData weapons (chara+0x710 + slot×0x70), 3 the summoned prop (+0x860).</summary>
    public static nint PropWeapon(nint character, nint c031)
    {
        nint data = *(nint*)(c031 + 0x48);
        int slot = *(ushort*)(data + 0xE);
        if (slot > 3)
            return 0;
        return slot < 3 ? *(nint*)(character + 0x710 + slot * 0x70) : *(nint*)(character + 0x860);
    }

    public static int PropTimelineId(nint c031) => *(ushort*)(*(nint*)(c031 + 0x48) + 0xC);

    /// <summary>The bucket-3 group keyed <c>W|2</c>, or 0.</summary>
    public nint PropGroup(nint weapon)
    {
        nint manager = _l.TimelineManager;
        if (weapon == 0 || manager == 0)
            return 0;
        int guard = 0;
        for (nint group = *(nint*)(manager + 0x30); group != 0 && guard < 256; group = *(nint*)(group + 0x10), guard++)
            if (*(ulong*)(group + 0x30) == ((ulong)weapon | 2))
                return group;
        return 0;
    }

    /// <summary>The group's CURRENT timeline: *(*(group+0xA48)), a refcounted
    /// handle node; the replaced one stays listed for a tick, so never "first".</summary>
    public static nint CurrentPropTimeline(nint group, int timelineId)
    {
        if (group == 0)
            return 0;
        nint handle = *(nint*)(group + 0xA48);
        nint timeline = handle != 0 ? *(nint*)handle : 0;
        return timeline != 0 && *(uint*)(timeline + 0x224) == (uint)(timelineId + 0x20000)
            && *(nint*)(timeline + 0x180) == handle ? timeline : 0;
    }

    /// <summary>Steps a prop timeline forward to <paramref name="target"/> in its
    /// own speed window and syncs its C009s; leaves it at
    /// <paramref name="ownerSpeed"/> (birth speed / per-frame push value).</summary>
    public void SeekProp(nint prop, float target, float ownerSpeed, HashSet<nint> synced)
    {
        OpenWindow(prop);
        nint control = _n.GetActorControl(prop);
        bool validControl = control != 0 && _l.IsActorControl(control);
        if (validControl)
            *(byte*)(control + 0x6B8) = 1;
        for (int i = 0; i < 32; i++)
        {
            float step = Math.Min(target - *(float*)(prop + 0x34), Length(prop));
            if (step <= 0.001f)
                break;
            _n.UpdateBanner(prop, step, 1);
            if ((*(byte*)(prop + 0x272) & 0x80) != 0)
                break;
        }
        if (validControl)
            SyncPropControls(prop, control, synced);
        CloseWindow(prop, ownerSpeed);
    }

    /// <summary>The prop's C009s: actor-control instance list (+0x30, next
    /// +0x10), clip at inst+0x60; each synced once (same guard as the body).</summary>
    public int SyncPropControls(nint prop, nint control, HashSet<nint> synced)
    {
        int count = 0, guard = 0;
        for (nint instance = *(nint*)(control + 0x30); instance != 0 && guard < 16; instance = *(nint*)(instance + 0x10), guard++)
        {
            nint clip = *(nint*)(instance + 0x60);
            if (clip == 0 || synced.Contains(clip) || _l.Classify(clip) != SeekClipKind.HavokAnimation
                || *(nint*)(clip + 0x98) != instance || !CanSync(clip))
                continue;
            float speed = OpenWindow(prop);
            SyncNoEpsilon(clip);
            CloseWindow(prop, speed);
            synced.Add(clip);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Re-fires a C031 whose start gave up (after a redraw the C031 activates
    /// while the new weapon object is still loading, and FUN_141909F80 drops it):
    /// only once W is ready (+0x89 &amp; 0xF == 3), only when its group has no
    /// current timeline (never replace a live one), never for scheduler-driven
    /// actors (+0x1CE8 &amp; 0x10 takes an unchecked path). The new timeline starts
    /// at 0 for non-portrait actors; the caller seeks it.
    /// </summary>
    public bool FireProp(nint character, nint c031)
    {
        if (!Ready || (*(byte*)(character + 0x1CE8) & 0x10) != 0)
            return false;
        nint weapon = PropWeapon(character, c031);
        if (weapon == 0 || (*(byte*)(weapon + 0x89) & 0xF) != 3)
            return false;
        nint group = PropGroup(weapon);
        if (group != 0 && *(nint*)(group + 0xA48) is var handle and not 0 && *(nint*)handle != 0)
            return false;
        _n.StartPropTimeline(c031);
        return true;
    }

    /// <summary>Starts the next clips on this timeline's actor control without a
    /// blend-in (+0x6B8 = 1; the game clears it after the next start).</summary>
    public void SetNoBlendStart(nint timeline)
    {
        nint control = _n.GetActorControl(timeline);
        if (control != 0 && _l.IsActorControl(control))
            *(byte*)(control + 0x6B8) = 1;
    }

    public nint PropControl(nint prop)
    {
        nint control = _n.GetActorControl(prop);
        return control != 0 && _l.IsActorControl(control) ? control : 0;
    }

    // ── Same-tick speed edges ─────────────────────────────────────────
    // The game gives a speed change to the actor's prop timelines directly (the
    // container update's per-frame push into the W|2 groups) but to its slot
    // timelines only through seq+0x154 plus a dirty bit, one Advance later, and
    // Poser writes the Havok controls immediately: three ticks (speedtrace
    // 2026-10-16). Setting every timeline in the same tick lines them up; the
    // game's later pushes find identical values and are change-guarded no-ops.

    /// <summary>The prop weapon objects: DrawData weapons 0–2 and the summoned
    /// prop (which gets no per-frame push at all).</summary>
    private static nint Weapon(nint character, int index) =>
        index < 3 ? *(nint*)(character + 0x710 + index * 0x70) : *(nint*)(character + 0x860);

    /// <summary>The slot timeline takes exactly what the sequencer's consumer
    /// would push from seq+0x154 one Advance later.</summary>
    public void SetSlotTimelineSpeed(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, int slot, float speed)
    {
        if (!_n.Ready)
            return;
        nint timeline = AnimationNativeState.SlotTimeline(sequencer, slot);
        if (timeline != 0)
            _n.SetSpeed(timeline, speed);
    }

    /// <summary>Every live timeline in the actor's W|2 prop groups takes the
    /// overall speed, walked like the game's TimelineGroup.SetSpeedAll: +0x18,
    /// next +0x88, skipping ended (+0x273 bit 0) and detached (+0x10 sign).</summary>
    public void SetPropSpeeds(nint character, float overall)
    {
        if (!Ready)
            return;
        for (int w = 0; w < 4; w++)
        {
            nint group = PropGroup(Weapon(character, w));
            int guard = 0;
            for (nint timeline = group != 0 ? *(nint*)(group + 0x18) : 0;
                 timeline != 0 && guard < 64;
                 timeline = *(nint*)(timeline + 0x88), guard++)
            {
                if ((*(byte*)(timeline + 0x273) & 1) != 0 || *(sbyte*)(timeline + 0x10) < 0)
                    continue;
                _n.SetSpeed(timeline, overall);
            }
        }
    }

    // ── Prop birth correction ─────────────────────────────────────────
    // A C031 fires in the step where the body crosses its start, so the prop
    // timeline is born at clock 0 while the body is already a fraction past it
    // (0.36–0.45 frames measured). The game corrects this only for portrait
    // actors (the elapsed offset gated on chara+0x190 & 8). One forward step on
    // the new timeline gives every actor what portraits get.

    public static nint WeaponAt(nint character, int index) => Weapon(character, index);

    /// <summary>
    /// Corrects a newly current prop timeline of <paramref name="weapon"/>.
    /// Returns false while it can't be decided yet (not loaded, no C031 found),
    /// so the caller retries next tick; true once handled either way.
    /// </summary>
    public bool CorrectPropBirth(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer,
        nint character, nint weapon, nint prop, out string? log)
    {
        log = null;
        if (!IsLoaded(prop))
            return false;
        int timelineId = (int)(*(uint*)(prop + 0x224) - 0x20000);
        foreach (int slot in new[] { 0, 1 })
        {
            nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
            if (root == 0)
                continue;
            foreach (var (clip, start) in Collect(root).Summons)
            {
                if (PropWeapon(character, clip) != weapon || PropTimelineId(clip) != timelineId)
                    continue;
                float err = (Clock(root) - start) - *(float*)(prop + 0x34);
                if (err <= 0.001f)
                    return true;
                if (err >= 2f)
                {
                    log = $"prop {timelineId} is {err:0.###} frames behind its body; left alone";
                    return true;
                }
                SeekProp(prop, Clock(root) - start, *(float*)(prop + 0x240), new HashSet<nint>());
                return true;
            }
        }
        return true; // not a summon this actor's slots play: nothing to correct
    }

    // ── VFX speed ─────────────────────────────────────────────────────
    // The C012 speed-changed handler (slot 35) forwards only speeds > 0, so
    // neither Poser's pause nor a speed window's restore to 0 ever reaches the
    // effects: they kept simulating next to a frozen actor. Paused effects are
    // set directly, and set back explicitly on resume (a completed C012 whose
    // effect still lives, like the bubbles, gets no speed event at all).

    /// <summary>A C012's VfxObject: inst=clip+0x98 → *(inst+0x28) → vfunc 0x68.</summary>
    private static nint VfxOf(nint clip)
    {
        nint instance = *(nint*)(clip + 0x98);
        nint holder = instance != 0 ? *(nint*)(instance + 0x28) : 0;
        if (holder == 0)
            return 0;
        var getVfx = (delegate* unmanaged<nint, nint>)(*(nint*)(*(nint*)holder + 0x68));
        return getVfx(holder);
    }

    /// <summary>Sets every live VFX object of a timeline tree to <paramref name="speed"/>.</summary>
    public void SetVfxSpeeds(nint root, float speed)
    {
        if (!Ready || root == 0)
            return;
        foreach (var clip in Collect(root).Vfx)
        {
            nint vfx = VfxOf(clip);
            if (vfx != 0 && *(float*)(vfx + 0x250) != speed)
                _n.SetVfxSpeed(vfx, speed);
        }
    }

    /// <summary>The actor's effects: slot timelines 0/1/2/3/6 and the live prop
    /// timelines of its four weapon objects.</summary>
    public void SetActorVfxSpeeds(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer,
        nint character, float speed)
    {
        if (!Ready)
            return;
        foreach (int slot in new[] { 0, 1, 2, 3, 6 })
            SetVfxSpeeds(AnimationNativeState.SlotTimeline(sequencer, slot), speed);
        for (int w = 0; w < 4; w++)
        {
            nint group = PropGroup(Weapon(character, w));
            int guard = 0;
            for (nint timeline = group != 0 ? *(nint*)(group + 0x18) : 0;
                 timeline != 0 && guard < 64;
                 timeline = *(nint*)(timeline + 0x88), guard++)
                if ((*(byte*)(timeline + 0x273) & 1) == 0 && *(sbyte*)(timeline + 0x10) >= 0)
                    SetVfxSpeeds(timeline, speed);
        }
    }

    // ── VFX orphan guard ──────────────────────────────────────────────

    /// <summary>Handles of this tree's live C012 VFX objects
    /// (inst=clip+0x98 → *(inst+0x28) → vfunc 0x68 → vfx; wrapper vfx+0x2A0; handle +0x60).</summary>
    public HashSet<ulong> VfxHandles(Tree tree)
    {
        var handles = new HashSet<ulong>();
        foreach (var clip in tree.Vfx)
        {
            nint vfx = VfxOf(clip);
            nint wrapper = vfx != 0 ? *(nint*)(vfx + 0x2A0) : 0;
            ulong handle = wrapper != 0 ? *(ulong*)(wrapper + 0x60) : 0;
            if (handle != 0)
                handles.Add(handle);
        }
        return handles;
    }

    /// <summary>Releases the handles this actor held before the seek that are
    /// still live (same generation, state != 3) but no longer held by any of its
    /// wrappers: engine instances a kill+respawn orphaned. Never touches anything
    /// outside <paramref name="before"/>.</summary>
    public int ReleaseOrphans(HashSet<ulong> before, HashSet<ulong> now)
    {
        nint js = _l.VfxSlotTable;
        nint table = js != 0 ? *(nint*)(js + 0x1498) : 0;
        if (table == 0)
            return 0;
        int released = 0;
        foreach (var handle in before)
        {
            if (now.Contains(handle))
                continue;
            uint index = (uint)(handle >> 32), generation = (uint)handle;
            if (index >= 0x800 || generation == 0)
                continue;
            nint entry = table + 0x2000 + (nint)index * 0x88;
            if (*(uint*)(entry + 0x60) != generation || *(ushort*)(entry + 0x58) == 3 || *(nint*)(entry + 0x30) == 0)
                continue;
            _l.ReleaseVfx(handle, 1);
            released++;
        }
        return released;
    }
}
