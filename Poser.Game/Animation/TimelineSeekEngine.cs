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
    /// <summary>ScheduleManagement (this frame's step in frames at +0x50), or 0.</summary>
    public nint ScheduleManagement => _l.ScheduleManagement;

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
        public readonly List<nint> Vfx = new();                         // C012/C049
        public readonly List<nint> AsyncVfx = new();                    // C173
        public readonly List<(nint Clip, float Start)> Summons = new(); // C031 + absolute start
        public readonly List<nint> WeaponRequests = new();              // C198
        /// <summary>Where the last C009 animation runs out on the root clock (max of
        /// absolute start + resolved length +0xB0), or 0 without C009s.</summary>
        public float PoseEnd;
        /// <summary>A C009 that hasn't resolved its length yet (not started): the
        /// pose end can still move out.</summary>
        public bool PoseEndPending;
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
                        if ((*(byte*)(clip + 0xCC) & 0x40) != 0)
                            tree.PoseEnd = Math.Max(tree.PoseEnd,
                                baseStart + *(float*)(clip + 0x50) + *(uint*)(clip + 0xB0));
                        else
                            tree.PoseEndPending = true;
                        break;
                    case SeekClipKind.Vfx:
                        tree.Vfx.Add(clip);
                        break;
                    case SeekClipKind.AsyncVfx:
                        tree.AsyncVfx.Add(clip);
                        break;
                    case SeekClipKind.SummonAnimation:
                        tree.Summons.Add((clip, baseStart + *(float*)(clip + 0x50)));
                        break;
                    case SeekClipKind.WeaponRequest:
                        tree.WeaponRequests.Add(clip);
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
    /// <summary>The clock runs in [begin +0x3C, end +0x40] (end = begin + length).</summary>
    public static float Begin(nint timeline) => *(float*)(timeline + 0x3C);
    public static float End(nint timeline) => *(float*)(timeline + 0x40);

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
    /// <param name="wrapForward">Wrap for forward targets too. A forward step only
    /// moves the timeline: an effect already alive is not advanced (the VFX engine
    /// runs on its own clock, frozen under pause), so it falls behind the body. The
    /// wrap re-spawns it at its exact age instead. The redraw restore starts from 0
    /// and doesn't need it.</param>
    /// <param name="blendFrames">A wrap that crossfades instead of cutting (the loop
    /// seam): the stopped C009s fade out and the restarted ones blend in over this
    /// many frames. 0 = the scrub's cut (no fade, no blend-in).</param>
    public string? Seek(nint character, nint root, float target, TimelineSeekWork work, bool wrapForward = true,
        int blendFrames = 0)
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

        // One frame short of the end: a C010 child placed exactly on its end fires
        // its end events but never completes.
        float begin = Begin(root), end = End(root);
        if (!(end > begin))
            end = begin + Length(root);
        target = Math.Clamp(target, begin, Math.Max(begin, end - 1f));
        var tree = Collect(root);
        work.Begin(character, root, VfxHandles(tree));

        float clock = Clock(root);
        // Forward only needs the wrap while an effect is alive: an in-place step
        // spawns the effects it crosses at their right age by itself.
        if (target < clock - 0.001f
            || (wrapForward && clock > 0.001f && target > clock + 0.001f && HasLiveVfx(tree)))
        {
            // A C031 the step re-fires replaces its prop timeline. The game stops the
            // replaced one later (its group update), so its holder would still be
            // bound and render the old prop pose on this frame. Stop it here, with
            // fade 0: tick N's pool update then releases it before the render.
            // A C031 starting after the target is not re-fired here, and stopping its
            // prop timeline releases the weapon request (C198 cell) that keeps the
            // prop weapon loaded: the weapon unloads and the C031 gives up at its
            // start. Hold that one instead; the C031 replaces it at its start.
            foreach (var (clip, start) in tree.Summons)
            {
                nint group = PropGroup(PropWeapon(character, clip));
                nint prop = CurrentPropTimeline(group, PropTimelineId(clip));
                if (prop == 0)
                    continue;
                if (target < start)
                {
                    _n.SetSpeed(prop, 0f);
                    continue;
                }
                *(uint*)(prop + 0x220) = 0;
                _n.StopTimeline(prop, 2, 0);
            }
            if (blendFrames > 0)
                BlendWrap(root, tree, blendFrames);
            else
                Wrap(root, tree);
        }
        float speed = OpenWindow(root);

        nint control = _n.GetActorControl(root);
        if (blendFrames == 0 && control != 0 && _l.IsActorControl(control))
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

    /// <summary>
    /// The wrap as a crossfade. Event 9 stops each C009 (child clips too) with fade
    /// = root +0x220 frames (int32). A restarted holder takes its blend-in from
    /// FUN_141BFF230(root): the play info's +0x38 when it is ≥ 0, else +0x21C
    /// (int32) while +0x272 bit 1 is set, else the blend table. The one in effect
    /// is set to the lead for the wrap and restored after it. The stopped controls
    /// keep advancing while they fade (into their end, as at the game's own seam
    /// into the next row).
    /// A holder restarted by its own timeline takes the old one's place with no
    /// blend (FUN_1418D6350's same-timeline branch: old holder +0x40 == the
    /// owner's instance id +0x70). The game's seam is a new timeline, so it never
    /// meets that branch; here the stopped holders' +0x40 is inverted between the
    /// stop and the restart, which sends the insert down the normal branch. They
    /// are dying holders, so nothing is restored.
    /// </summary>
    private void BlendWrap(nint root, Tree tree, int frames)
    {
        // The stop clears inst+0x78: collect the holders first.
        var holders = new List<nint>();
        foreach (var clip in tree.Havok.Keys)
        {
            nint instance = *(nint*)(clip + 0x98);
            nint holder = instance != 0 ? *(nint*)(instance + 0x78) : 0;
            if (holder != 0)
                holders.Add(holder);
        }
        nint playInfo = PlayInfo(root);
        bool viaPlayInfo = playInfo != 0 && *(int*)(playInfo + 0x38) >= 0;
        LastBlendPath = viaPlayInfo ? "play info +0x38" : "+0x21C";
        int fade = *(int*)(root + 0x220);
        int blend = viaPlayInfo ? *(int*)(playInfo + 0x38) : *(int*)(root + 0x21C);
        byte flags = *(byte*)(root + 0x272);
        // The stopped holders' own fade-to-empty multiplies into the blend node's
        // crossfade (old weight (1 − x)², the sum dipped to ~0.77 mid-seam); 0 drops
        // them at once (the body fell to the new control's ~0 weight: a bind-pose
        // frame). A fade far longer than the blend leaves only the crossfade, the
        // sum stays 1 as at the game's own seam, and the node releases them when
        // the blend completes (measured: removed the frame after it). Only C009s:
        // the VFX clips' stop reads the same field, and 1000 left the old emitters
        // fading for ~33 s (the stuck bubbles). They stop with 0, as in a scrub.
        if (viaPlayInfo)
            *(int*)(playInfo + 0x38) = frames;
        else
        {
            *(int*)(root + 0x21C) = frames;
            *(byte*)(root + 0x272) = (byte)(flags | 2);
        }
        try
        {
            StopAll(root, clip => *(int*)(root + 0x220) =
                _l.Classify(clip) == SeekClipKind.HavokAnimation ? OldHolderFadeFrames : 0);
            foreach (var holder in holders)
                *(uint*)(holder + 0x40) = ~*(uint*)(holder + 0x40);
            Rewind(root, tree);
        }
        finally
        {
            *(int*)(root + 0x220) = fade;
            if (viaPlayInfo)
                *(int*)(playInfo + 0x38) = blend;
            else
            {
                *(int*)(root + 0x21C) = blend;
                // Rewind rewrites bit 7 of +0x272; only bit 1 goes back.
                *(byte*)(root + 0x272) = (byte)((*(byte*)(root + 0x272) & ~2) | (flags & 2));
            }
        }
    }

    private const int OldHolderFadeFrames = 1000;

    /// <summary>The root's play info (vfunc 0x118: root+0x110 while +0x271 bit 6), or 0.</summary>
    public static nint PlayInfo(nint root)
    {
        var playInfoOf = (delegate* unmanaged<nint, nint>)(*(nint*)(*(nint*)root + 0x118));
        return playInfoOf(root);
    }

    /// <summary>
    /// The frames the root's C009s have left, as the sequencer's pre-end notify
    /// reads them (FUN_1418FF650, which can't be called: it may send the notify
    /// itself): the max of each C009's vfunc 0x98 (read-only; it resolves the
    /// length if needed), skipping clips marked +0x10 bit 7; 0 once the root is
    /// ended (+0x273 bit 0); −1 without C009s. In the clip's own units: a
    /// stretched clip (an expression's 1-frame face at 1/150) counts down slower
    /// than the timeline, so callers measure its rate rather than assume it.
    /// </summary>
    public float Remaining(nint root)
    {
        if ((*(byte*)(root + 0x273) & 1) != 0)
            return 0f;
        float remaining = -1f;
        foreach (var clip in Collect(root).Havok.Keys)
        {
            if ((*(byte*)(clip + 0x10) & 0x80) != 0)
                continue;
            // No pose: a conditional variant its owner doesn't play (started with
            // anim id 0, inst+0x40 == 0, whose 0x98 returns its full length for
            // ever), or a refused holder. The game's finalization doesn't wait on them.
            nint instance = *(nint*)(clip + 0x98);
            if (instance != 0 && (*(int*)(instance + 0x40) == 0 ||
                (*(nint*)(instance + 0x78) == 0 && *(byte*)(instance + 0x3A) == 1)))
                continue;
            var remainingOf = (delegate* unmanaged<nint, float>)(*(nint*)(*(nint*)clip + 0x98));
            remaining = Math.Max(remaining, Math.Max(0f, remainingOf(clip)));
        }
        return remaining;
    }

    /// <summary>
    /// Whether the root would finalize on its next Advance: every track done
    /// (FUN_1418FD950's walk, which sends the end notify itself). Read-only:
    /// a track is done if +0x10 bit 7, else it needs +0x10 bit 0 and every clip
    /// done; a C009 by bit 7; a C010 by bit 7, its +0x140 node's +0x84 bit 1, its
    /// +0x165, or (bit 0 and its child timeline finalized); other clips by bit 7
    /// or not bit 0.
    /// </summary>
    public bool Finalized(nint timeline, int depth = 0)
    {
        if (depth > 4)
            return false;
        bool any = false;
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
                any = true;
                byte trackFlags = *(byte*)(track + 0x10);
                if ((trackFlags & 0x80) != 0)
                    continue;
                if ((trackFlags & 1) == 0)
                    return false;
                nint clips = *(nint*)(track + 0x18);
                int clipCount = *(ushort*)(track + 0x22);
                for (int c = 0; clips != 0 && c < clipCount && c < 256; c++)
                {
                    nint clip = *(nint*)(clips + c * 8);
                    if (clip != 0 && !ClipFinalized(clip, depth))
                        return false;
                }
            }
        }
        return any;
    }

    private bool ClipFinalized(nint clip, int depth)
    {
        byte flags = *(byte*)(clip + 0x10);
        if ((flags & 0x80) != 0)
            return true;
        switch (_l.Classify(clip))
        {
            case SeekClipKind.HavokAnimation:
                return false;
            case SeekClipKind.ChildTimeline:
                nint node = *(nint*)(clip + 0x140);
                if (node != 0 && ((*(uint*)(node + 0x84) >> 1) & 1) != 0)
                    return true;
                if (*(byte*)(clip + 0x165) != 0)
                    return true;
                return (flags & 1) != 0 && ChildOf(clip) is var child and not 0 && Finalized(child, depth + 1);
            default:
                return (flags & 1) == 0;
        }
    }

    /// <summary>Which blend-in source the last crossfade wrap wrote (for the log).</summary>
    public string? LastBlendPath { get; private set; }

    /// <param name="beforeStop">Runs before each clip's event 9 (sets the fade it reads).</param>
    private void StopAll(nint root, Action<nint>? beforeStop = null)
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
                beforeStop?.Invoke(clip);
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
                || *(nint*)(clip + 0x98) != instance)
                continue;
            // A prop clip that just started (the C031 re-fire) has no length yet;
            // resolve it so it is synced now, not a frame later at frame 0.
            if (*(nint*)(instance + 0x78) != 0 && (*(byte*)(clip + 0xCC) & 0x40) == 0)
                _n.ResolveHavokLength(clip);
            if (!CanSyncNow(clip))
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

    /// <summary>A prop weapon's own Havok controls (W is its CharacterBase) at a
    /// speed. The game re-pushes the overall speed onto the prop TIMELINES at the end
    /// of every container update, after Poser's hook, so a layer's prop speed can
    /// only be held on its controls.</summary>
    public static void SetWeaponControlSpeeds(nint weapon, float speed)
    {
        if (weapon == 0)
            return;
        var weaponBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)weapon;
        var skeleton = weaponBase->Skeleton;
        if (skeleton == null)
            return;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
            for (int k = 0; k < 2; k++)
            {
                var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(k);
                if (animated == null)
                    continue;
                for (int c = 0; c < animated->AnimationControls.Length; c++)
                    if (animated->AnimationControls[c].Value is var control && control != null)
                        control->PlaybackSpeed = speed;
            }
    }

    /// <summary>Every live timeline in one prop weapon's W|2 group at a speed (the
    /// summoned prop's group, which the container update never pushes).</summary>
    public void SetWeaponTimelineSpeeds(nint weapon, float speed)
    {
        if (!Ready)
            return;
        nint group = PropGroup(weapon);
        int guard = 0;
        for (nint timeline = group != 0 ? *(nint*)(group + 0x18) : 0;
             timeline != 0 && guard < 64;
             timeline = *(nint*)(timeline + 0x88), guard++)
            if ((*(byte*)(timeline + 0x273) & 1) == 0 && *(sbyte*)(timeline + 0x10) >= 0)
                _n.SetSpeed(timeline, speed);
    }

    /// <summary>The C031's weapon slot (u16 data+0xE): 0–2 DrawData, 3 the summoned prop.</summary>
    public static int PropWeaponSlot(nint c031) => *(ushort*)(*(nint*)(c031 + 0x48) + 0xE);

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

    // ── Layer weight ──────────────────────────────────────────────────
    // The game has no per-slot weight. Each C009's animation holder
    // (inst = clip+0x98, holder = inst+0x78) multiplies its blend-tree weight by
    // holder+0x5C every pool update (FUN_1418FB860 → SetWeight, clamped to
    // [0, 1]); only holder init writes it (1.0). Holding it per slot never fights
    // the game; new holders (a restart, a new clip) need it again, so it is
    // re-applied every tick.

    /// <summary>Sets every holder of the slot timeline's tree to <paramref name="weight"/>.</summary>
    public void SetSlotWeight(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, nint character, int slot, float weight,
        IReadOnlySet<nint>? suppressed = null)
    {
        if (!Ready)
            return;
        nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
        if (root == 0)
            return;
        var tree = Collect(root);
        foreach (var clip in tree.Havok.Keys)
            SetHolderWeight(*(nint*)(clip + 0x98), weight);
        // Props the layer summons (C031) run on their own bucket-3 timelines; their
        // C009 holders hang off the prop's actor control instance list. A prop held
        // invisible because a higher layer owns its weapon stays at 0.
        foreach (var (clip, _) in tree.Summons)
        {
            nint weapon = PropWeapon(character, clip);
            nint prop = CurrentPropTimeline(PropGroup(weapon), PropTimelineId(clip));
            if (prop != 0 && (suppressed == null || !suppressed.Contains(prop)))
                SetPropTimelineWeight(prop, weight);
        }
    }

    /// <summary>Every C009 holder of one prop timeline at a weight.</summary>
    public void SetPropTimelineWeight(nint prop, float weight)
    {
        nint control = prop != 0 ? PropControl(prop) : 0;
        int guard = 0;
        for (nint instance = control != 0 ? *(nint*)(control + 0x30) : 0; instance != 0 && guard < 16;
             instance = *(nint*)(instance + 0x10), guard++)
            SetHolderWeight(instance, weight);
    }

    /// <summary>The group's current prop timeline whatever its id, or 0.</summary>
    public static nint CurrentPropTimelineAny(nint group)
    {
        if (group == 0)
            return 0;
        nint handle = *(nint*)(group + 0xA48);
        nint timeline = handle != 0 ? *(nint*)handle : 0;
        return timeline != 0 && *(nint*)(timeline + 0x180) == handle ? timeline : 0;
    }

    /// <summary>The ActionTimeline id a prop timeline plays (+0x224 − 0x20000).</summary>
    public static int PropTimelineIdOf(nint prop) => (int)(*(uint*)(prop + 0x224) - 0x20000);

    /// <summary>Fires a C031 over whatever prop timeline its weapon has (the start
    /// replaces it, as a natural fire does); needs the weapon loaded.</summary>
    public bool ReFireProp(nint character, nint c031)
    {
        if (!Ready || (*(byte*)(character + 0x1CE8) & 0x10) != 0)
            return false;
        nint weapon = PropWeapon(character, c031);
        if (weapon == 0 || (*(byte*)(weapon + 0x89) & 0xF) != 3)
            return false;
        _n.StartPropTimeline(c031);
        return true;
    }

    private static void SetHolderWeight(nint instance, float weight)
    {
        nint holder = instance != 0 ? *(nint*)(instance + 0x78) : 0;
        if (holder != 0 && *(float*)(holder + 0x5C) != weight)
            *(float*)(holder + 0x5C) = weight;
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
    private static nint VfxOf(nint clip) => VfxOfInstance(*(nint*)(clip + 0x98));

    private static nint VfxOfInstance(nint instance)
    {
        nint holder = instance != 0 ? *(nint*)(instance + 0x28) : 0;
        if (holder == 0)
            return 0;
        var getVfx = (delegate* unmanaged<nint, nint>)(*(nint*)(*(nint*)holder + 0x68));
        return getVfx(holder);
    }

    /// <summary>Every VFX object of a tree: one per C012/C049, up to 8 per C173.</summary>
    private static List<nint> VfxObjects(Tree tree)
    {
        var objects = new List<nint>();
        foreach (var clip in tree.Vfx)
            if (VfxOf(clip) is var vfx and not 0)
                objects.Add(vfx);
        foreach (var clip in tree.AsyncVfx)
        {
            nint array = *(nint*)(clip + 0xD8);
            nint data = *(nint*)(clip + 0x48);
            if (array == 0 || data == 0)
                continue;
            int count = Math.Clamp(*(int*)(data + 0x18), 1, 8);
            for (int i = 0; i < count; i++)
                if (VfxOfInstance(*(nint*)(array + i * 8)) is var vfx and not 0)
                    objects.Add(vfx);
        }
        return objects;
    }

    /// <summary>Sets every live VFX object of a timeline tree to <paramref name="speed"/>.</summary>
    public void SetVfxSpeeds(nint root, float speed)
    {
        if (!Ready || root == 0)
            return;
        foreach (var vfx in VfxObjects(Collect(root)))
            if (*(float*)(vfx + 0x250) != speed)
                _n.SetVfxSpeed(vfx, speed);
    }

    /// <summary>The weapon objects' own model VFX (glows, idle effects).</summary>
    public void SetWeaponVfxSpeeds(nint character, float speed)
    {
        if (!Ready)
            return;
        for (int w = 0; w < 4; w++)
            if (Weapon(character, w) is var weapon and not 0)
                _n.SetModelVfxSpeed(weapon, speed);
    }

    /// <summary>The actor's effects: slot timelines 0/1/2/3/6 and the live prop
    /// timelines of its four weapon objects.</summary>
    public void SetActorVfxSpeeds(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer,
        nint character, float speed)
    {
        if (!Ready)
            return;
        for (int slot = 0; slot < 14; slot++)
            SetVfxSpeeds(AnimationNativeState.SlotTimeline(sequencer, slot), speed);
        SetWeaponVfxSpeeds(character, speed);
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
        foreach (var vfx in VfxObjects(tree))
        {
            nint wrapper = *(nint*)(vfx + 0x2A0);
            ulong handle = wrapper != 0 ? *(ulong*)(wrapper + 0x60) : 0;
            if (handle != 0)
                handles.Add(handle);
        }
        return handles;
    }

    /// <summary>Whether any of the tree's C012 effects still has a live engine
    /// instance (same generation, not released) in the Apricot slot table.</summary>
    private bool HasLiveVfx(Tree tree)
    {
        nint js = _l.VfxSlotTable;
        nint table = js != 0 ? *(nint*)(js + 0x1498) : 0;
        if (table == 0)
            return tree.Vfx.Count + tree.AsyncVfx.Count > 0;
        foreach (var handle in VfxHandles(tree))
        {
            uint index = (uint)(handle >> 32), generation = (uint)handle;
            if (index >= 0x800 || generation == 0)
                continue;
            nint entry = table + 0x2000 + (nint)index * 0x88;
            if (*(uint*)(entry + 0x60) == generation && *(ushort*)(entry + 0x58) != 3 && *(nint*)(entry + 0x30) != 0)
                return true;
        }
        return false;
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
