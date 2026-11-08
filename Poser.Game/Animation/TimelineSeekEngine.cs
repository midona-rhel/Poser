using System;
using System.Collections.Generic;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// Moves a live animation timeline using the game's scheduler on the framework
/// thread. Forward seeks advance it; backward seeks stop and restart clips before
/// advancing to the target. Native clip ordering matters for props and effects.
/// TimelineSeekWork finishes clips and props that need later updates.
/// </summary>
internal sealed unsafe partial class TimelineSeekEngine
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
        nint actor = *(nint*)(timeline + NativeFields.SchedulerTimeline_Actors);
        for (int a = 0; actor != 0 && a < 32; a++, actor = *(nint*)(actor + NativeFields.TmbActor_Next))
        {
            nint tracks = *(nint*)(actor + NativeFields.TmbActor_Tracks);
            int trackCount = *(ushort*)(actor + NativeFields.TmbActor_TrackCount);
            for (int t = 0; tracks != 0 && t < trackCount && t < 64; t++)
            {
                nint track = *(nint*)(tracks + t * 8);
                if (track == 0)
                    continue;
                nint clips = *(nint*)(track + NativeFields.TmbTrack_Clips);
                int clipCount = *(ushort*)(track + NativeFields.TmbTrack_ClipCount);
                for (int c = 0; clips != 0 && c < clipCount && c < 256; c++)
                {
                    nint clip = *(nint*)(clips + c * 8);
                    if (clip != 0 && *(nint*)(clip + NativeFields.SchedulerClip_Data) != 0)
                        into.Add(clip);
                }
            }
        }
    }

    /// <summary>A C010's child timeline, or 0.</summary>
    private nint ChildOf(nint c010)
    {
        nint child = *(nint*)(c010 + NativeFields.TimelineClip_Child);
        return child != 0 && _l.IsChildTimeline(child) ? child : 0;
    }

    /// <summary>Everything one seek touches, collected from the slot timeline's tree.</summary>
    internal sealed class Tree
    {
        public readonly List<(nint Clip, nint Child)> C010s = new();
        public readonly List<float> C010Starts = new();                 // absolute
        public readonly HashSet<nint> NestedC010s = new();              // not on the root
        public readonly Dictionary<nint, nint> Havok = new();          // C009 → owner timeline
        public readonly Dictionary<nint, float> HavokStart = new();    // C009 → absolute start
        // C009 → root frames per frame of its own timeline (1 on the root; a range
        // C010's child runs 0..1 over the C010's window: 1/scale).
        public readonly Dictionary<nint, float> HavokScale = new();
        // C009 → its window in its own timeline's frames when that timeline is a
        // range C010's child (data+0xC bit 0): the range, data+0x14 − data+0x10
        // (C009 vfunc 0x140), not the clip's +0x54.
        public readonly Dictionary<nint, float> HavokRange = new();
        public readonly List<nint> Vfx = new();                         // C012/C049
        public readonly List<nint> AsyncVfx = new();                    // C173
        public readonly List<(nint Clip, float Start)> Summons = new(); // C031 + absolute start
        public readonly List<nint> WeaponRequests = new();              // C198
        public readonly List<nint> SummonWeapons = new();               // C043
        public readonly List<nint> Invisibilities = new();              // C094
        /// <summary>Where the last C009 animation runs out on the root clock (max of
        /// absolute start + resolved length +0xB0), or 0 without C009s.</summary>
        public float PoseEnd;
        /// <summary>A C009 that hasn't resolved its length yet (not started): the
        /// pose end can still move out.</summary>
        public bool PoseEndPending;
    }

    // One frame's trees, while the service's tick runs (BeginFrameTrees..EndFrameTrees):
    // the props, loops, owned layers and clamped controls each walked the same slot
    // timelines every frame. A tree holds clip pointers and their static data; only a
    // seek (or a new timeline, a new root) changes it, and Seek drops its root's entry.
    // Outside the tick (the speed and weight holds in the game's update, readings
    // from the UI) every call walks afresh: a replaced timeline can reuse an address.
    private readonly Dictionary<nint, Tree> _frameTrees = new();
    private bool _frameTreesOn;

    public void BeginFrameTrees()
    {
        _frameTrees.Clear();
        _frameTreesOn = true;
    }

    public void EndFrameTrees()
    {
        _frameTrees.Clear();
        _frameTreesOn = false;
    }

    public Tree Collect(nint root)
    {
        if (_frameTreesOn && _frameTrees.TryGetValue(root, out var cached))
            return cached;
        var built = Build(root);
        if (_frameTreesOn)
            _frameTrees[root] = built;
        return built;
    }

    private Tree Build(nint root)
    {
        var tree = new Tree();
        var seen = new HashSet<nint>();
        var clips = new List<nint>();
        // A timeline's local frame f is root frame baseStart + span·f. A C010 child's
        // clock is (parent − start)·scale(+0x160) + offset (data+0x10 when data+0xC
        // bit 0), so its frame f is parent frame start + (f − offset)/scale.
        void Walk(nint t, float baseStart, int depth, float span = 1f, float range = -1f)
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
                        tree.C010Starts.Add(baseStart + span * *(float*)(clip + NativeFields.SchedulerClip_StartFrame));
                        if (depth > 0)
                            tree.NestedC010s.Add(clip);
                        float scale = *(float*)(clip + NativeFields.TimelineClip_Scale);
                        if (!(scale > 0f) || !float.IsFinite(scale))
                            scale = 1f;
                        nint c010Data = *(nint*)(clip + NativeFields.SchedulerClip_Data);
                        float offset = c010Data != 0 && (*(uint*)(c010Data + NativeFields.C010Data_Flags) & 1) != 0 ? *(float*)(c010Data + NativeFields.C010Data_Offset) : 0f;
                        float childRange = c010Data != 0 && (*(uint*)(c010Data + NativeFields.C010Data_Flags) & 1) != 0
                            ? *(float*)(c010Data + NativeFields.C010Data_RangeEnd) - *(float*)(c010Data + NativeFields.C010Data_Offset)
                            : -1f;
                        Walk(child, baseStart + span * (*(float*)(clip + NativeFields.SchedulerClip_StartFrame) - offset / scale), depth + 1, span / scale, childRange);
                        break;
                    case SeekClipKind.HavokAnimation:
                        tree.Havok[clip] = t;
                        tree.HavokStart[clip] = baseStart + span * *(float*)(clip + NativeFields.SchedulerClip_StartFrame);
                        tree.HavokScale[clip] = span;
                        if (range > 0f)
                            tree.HavokRange[clip] = range;
                        // A clip without a pose (see Remaining) never plays; the
                        // timeline ends without it.
                        if (NoPose(clip))
                            break;
                        if ((*(byte*)(clip + NativeFields.HavokAnimationClip_Flags) & 0x40) != 0)
                            tree.PoseEnd = Math.Max(tree.PoseEnd,
                                baseStart + span * (*(float*)(clip + NativeFields.SchedulerClip_StartFrame) + *(uint*)(clip + NativeFields.HavokAnimationClip_LengthFrames)));
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
                        tree.Summons.Add((clip, baseStart + span * *(float*)(clip + NativeFields.SchedulerClip_StartFrame)));
                        break;
                    case SeekClipKind.WeaponRequest:
                        tree.WeaponRequests.Add(clip);
                        break;
                    case SeekClipKind.SummonWeapon:
                        tree.SummonWeapons.Add(clip);
                        break;
                    case SeekClipKind.Invisibility:
                        tree.Invisibilities.Add(clip);
                        break;
                }
            }
        }
        Walk(root, 0f, 0);
        return tree;
    }

    // ── Clock ─────────────────────────────────────────────────────────


    // ── Speed window ──────────────────────────────────────────────────
    // Advance multiplies dt by tl+0x240 and the clip sync multiplies the whole
    // elapsed by the ROOT speed, so both run at exactly 1.0. Poser's pause is
    // speed 0; the caller restores it. Never window a C010 child: its own +0x240
    // is an unused denormal, and SetSpeed(child, x) fires 0x1E(x) into the
    // child's C012s, freezing their VFX (the stuck bubble emitter).

    private float OpenWindow(nint root)
    {
        float speed = TimelineState.Of(root).Speed;
        if (speed != 1f)
            _n.SetSpeed(root, 1f);
        return speed;
    }

    private void CloseWindow(nint root, float speed)
    {
        if (TimelineState.Of(root).Speed != speed)
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
            *(byte*)(clip + NativeFields.HavokAnimationClip_Flags) &= 0xFE;
            return;
        }
        float epsilon = *(float*)(schedule + NativeFields.ScheduleManagement_FrameDelta);
        *(float*)(schedule + NativeFields.ScheduleManagement_FrameDelta) = 0f;
        try { _n.SyncLocalTime(clip); }
        finally { *(float*)(schedule + NativeFields.ScheduleManagement_FrameDelta) = epsilon; }
        // +0xCC bit 0 = "post-start sync pending", read only by the C009 post-steps
        // (slots 51/52). A clip restarted inside the seek kept it (its own post-step
        // failed the speed-pending guard), so the next tick re-synced it at the
        // paused root speed 0, i.e. to its frame 0: the backward-scrub flash.
        *(byte*)(clip + NativeFields.HavokAnimationClip_Flags) &= 0xFE;
    }


    /// <summary>The game's own guard before a C009 sync: an instance, not
    /// finished (+0x34 bit 5), and past its first state.</summary>
    private static bool CanSync(nint c009)
    {
        nint instance = *(nint*)(c009 + NativeFields.HavokAnimationClip_AnimObject);
        return instance != 0 && (*(uint*)(instance + NativeFields.ClipAnimation_Bits) & 0x20) == 0 && LengthKnown(c009);
    }

    /// <summary>The game syncs only +0xB0 > 1; a face clip's PAP reports 1 frame and
    /// free-runs from its Start in play. FUN_14190DE90 never reads +0xB0, so a
    /// resolved length (+0xCC bit 6) is enough for a seek to place it.</summary>
    private static bool LengthKnown(nint c009) =>
        *(uint*)(c009 + NativeFields.HavokAnimationClip_LengthFrames) > 1 || (*(byte*)(c009 + NativeFields.HavokAnimationClip_Flags) & 0x40) != 0;

    private static bool CanSyncNow(nint c009)
    {
        nint instance = *(nint*)(c009 + NativeFields.HavokAnimationClip_AnimObject);
        return instance != 0 && *(nint*)(instance + NativeFields.ClipAnimation_Holder) != 0 && LengthKnown(c009);
    }

    /// <summary>Synchronizes eligible C009 animation clips at root speed 1. Returns the
    /// number synchronized; adds clips that are not ready to pending.</summary>
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
        if (!TimelineState.Of(root).Loaded)
            return "timeline still loading";
        _frameTrees.Remove(root);
        if (TimelineState.Of(root).IntegerFrame)
            return "integer-frame timeline";
        if (TimelineState.Of(root).Finished)
            return "timeline already finished";
        if (TimelineState.Of(root).ClampedAtEnd)
            return "one-shot clamped at its end (seeked by its controls)";
        // Any pause bit besides the game's own bit 2 freezes Advance: no step.
        if ((TimelineState.Of(root).PauseMask & ~4u) != 0)
            return "timeline is held by a pause bit";

        // One frame short of the end: a C010 child placed exactly on its end fires
        // its end events but never completes.
        float begin = TimelineState.Of(root).Begin, end = TimelineState.Of(root).End;
        if (!(end > begin))
            end = begin + TimelineState.Of(root).Length;
        target = Math.Clamp(target, begin, Math.Max(begin, end - 1f));
        var tree = Collect(root);
        work.Begin(character, root, VfxHandles(tree));

        float clock = TimelineState.Of(root).Clock;
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
                *(uint*)(prop + NativeFields.SchedulerTimeline_StopFadeFrames) = 0;
                _n.StopTimeline(prop, 2, 0);
            }
            if (blendFrames > 0)
                BlendWrap(root, tree, blendFrames);
            else
            {
                // The frame-0 activation restarts the C009s: without a blend-in only
                // when +0x6B8 is set before it.
                nint wrapControl = _n.GetActorControl(root);
                if (wrapControl != 0 && _l.IsActorControl(wrapControl))
                    *(byte*)(wrapControl + NativeFields.ActorControl_NoBlendStart) = 1;
                Wrap(root, tree);
            }
        }
        float speed = OpenWindow(root);

        nint control = _n.GetActorControl(root);
        if (blendFrames == 0 && control != 0 && _l.IsActorControl(control))
            *(byte*)(control + NativeFields.ActorControl_NoBlendStart) = 1; // start the re-entered clips without a blend-in
        // A C010 started inside a step is stepped by the step's whole dt on top
        // (its post-step runs once per Advance, after the sub-steps): it overshoots
        // by the time from the step's begin to its start, and a short range child
        // (each face clip) runs out at once. Steps end half a frame before each
        // C010 start, so every start lands in its step's first sub-step, as in play.
        var c010Starts = tree.C010Starts;
        c010Starts.Sort();
        for (int i = 0; i < 400; i++)
        {
            float now = *(float*)(root + NativeFields.TimelineController_CurrentTimestamp);
            float stop = target;
            foreach (float start in c010Starts)
                if (start - 0.5f > now + 0.001f && start <= target)
                {
                    stop = start - 0.5f;
                    break;
                }
            float step = Math.Min(stop - now, TimelineState.Of(root).Length);
            if (step <= 0.001f)
                break;
            // UpdateBanner clears +0x6B8 at its end: every chunk needs it again, or
            // the clips started in later chunks blend in (and a pause freezes the
            // blend half-way).
            if (blendFrames == 0 && control != 0 && _l.IsActorControl(control))
                *(byte*)(control + NativeFields.ActorControl_NoBlendStart) = 1;
            _n.UpdateBanner(root, step, 1);
            if ((*(byte*)(root + NativeFields.SchedulerTimeline_Flags272) & 0x80) != 0)
                break;
        }

        // Re-collect: the step may have created clips' instances (C009s that just
        // started, C031 prop timelines, C012 VFX objects).
        _frameTrees.Remove(root);
        tree = Collect(root);
        SettleHavok(tree, target, blendFrames == 0 && control != 0 && _l.IsActorControl(control) ? control : 0);
        // C009s started inside the step have no length (+0xB0) until their first
        // post-step next frame; resolve it now so the new control is synced on
        // the seek frame itself.
        foreach (var clip in tree.Havok.Keys)
        {
            nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
            if (instance != 0 && *(nint*)(instance + NativeFields.ClipAnimation_Holder) != 0 && (*(byte*)(clip + NativeFields.HavokAnimationClip_Flags) & 0x40) == 0)
                _n.ResolveHavokLength(clip);
        }
        SyncHavok(root, tree.Havok.Keys, work.PendingHavok, immediate: true);
        CloseWindow(root, speed);
        StepPoolUnderPause(character, control);

        work.OwnerSpeed = speed;
        foreach (var (clip, start) in tree.Summons)
            work.Props.Add(new TimelineSeekWork.Prop(clip, start));
        work.PumpProps(this);
        // The window fired 0x1E(1) into the C012s and the restore to 0 was dropped.
        if (speed == 0f)
            work.FreezeVfx(this);
        _frameTrees.Remove(root);
        return null;
    }

    /// <summary>
    /// The holder pool steps by dt × OverallSpeed (§8.3): under a whole-actor pause
    /// the restart's blend and the replaced holder's fade stand still, and the old
    /// control keeps the pose. One pool step at speed 1 completes them.
    /// </summary>
    private void StepPoolUnderPause(nint character, nint control)
    {
        if (_n.UpdatePool == null || control == 0 || !_l.IsActorControl(control))
            return;
        ref float overall = ref ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)character)->Timeline.OverallSpeed;
        if (overall != 0f || _n.ActorControlCharacter(control) != character)
            return;
        float saved = overall;
        overall = 1f;
        // In frames: a crossfade the play started (its blend has a length) needs
        // more than one. This uses a fixed 30-frame step to finish the blend.
        try { _n.UpdatePool(control, 30f, 0); }
        finally { overall = saved; }
    }

    /// <summary>
    /// After the step, the C009s the way normal play would leave them at the target
    /// (animation-system.md §15.3): one whose window is over but that is still
    /// active is stopped with reason 0xE, as the game's catch-up does with finished
    /// animations; one active at the target without a holder (its creation refused,
    /// face clips starting in another order than in play) is stopped (2) and started
    /// again. Conditional variants its owner doesn't play (anim id 0) are left.
    /// The face otherwise stayed on its idle after a seek.
    /// </summary>
    private void SettleHavok(Tree tree, float target, nint noBlendControl)
    {
        foreach (var (clip, start) in tree.HavokStart)
        {
            // Only clips the scheduler already started (+0x10 bit 4; their 0x30 is
            // set, so a direct Start skips nothing the pending path would do).
            if ((*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x10) == 0 || start > target)
                continue;
            nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
            if (instance == 0)
                continue;
            var stop = (delegate* unmanaged<nint, int, byte>)(*(nint*)(*(nint*)clip + 32 * 8));
            // The window: the TMB duration (+0x54) from the start, or less where the
            // animation (+0xB0 frames at rate +0xBC) runs out first.
            if ((*(byte*)(clip + NativeFields.HavokAnimationClip_Flags) & 0x40) != 0)
            {
                float rate = *(float*)(clip + NativeFields.HavokAnimationClip_Rate);
                float window = tree.HavokRange.TryGetValue(clip, out var range) ? range : *(float*)(clip + NativeFields.SchedulerClip_Duration);
                if (rate > 0f && !tree.HavokRange.ContainsKey(clip))
                    window = Math.Min(window, *(uint*)(clip + NativeFields.HavokAnimationClip_LengthFrames) / rate);
                // In the clip's own timeline's frames: a range C010's child is scaled.
                window *= tree.HavokScale.GetValueOrDefault(clip, 1f);
                if (window > 0f && start + window <= target)
                {
                    stop(clip, 0xE);
                    continue;
                }
            }
            if (*(nint*)(instance + NativeFields.ClipAnimation_Holder) != 0 || *(int*)(instance + NativeFields.ClipAnimation_AnimId) == 0)
                continue;
            // A scrub's restart enters at full weight (the step's +0x6B8 was cleared
            // by UpdateBanner); a loop wrap's keeps its crossfade.
            if (noBlendControl != 0)
                *(byte*)(noBlendControl + NativeFields.ActorControl_NoBlendStart) = 1;
            var startClip = (delegate* unmanaged<nint, int, void>)(*(nint*)(*(nint*)clip + 31 * 8));
            stop(clip, 2);
            startClip(clip, 2);
            if (noBlendControl != 0)
                *(byte*)(noBlendControl + NativeFields.ActorControl_NoBlendStart) = 0;
        }
    }


    /// <summary>The Havok controls a C009 drives: instance → holder +0x20 handle →
    /// its controller set (+0x18, a tree) → each controller's control (+0x38).</summary>
    public static List<nint> ClipControls(nint clip)
    {
        var list = new List<nint>();
        nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
        nint holder = instance != 0 ? *(nint*)(instance + NativeFields.ClipAnimation_Holder) : 0;
        nint handle = holder != 0 ? *(nint*)(holder + NativeFields.AnimationHolder_Handle) : 0;
        if (handle == 0)
            return list;
        nint head = *(nint*)(handle + NativeFields.AnimationControllerHandle_Controllers);
        if (head == 0)
            return list;
        void Walk(nint node, int depth)
        {
            if (node == 0 || node == head || depth > 16 || *(byte*)(node + NativeFields.StdTreeNode_IsNil) != 0 || list.Count > 32)
                return;
            Walk(*(nint*)node, depth + 1);
            nint controller = *(nint*)(node + NativeFields.StdTreeNode_Value);
            if (controller != 0 && *(nint*)(controller + NativeFields.AnimationController_Control) != 0)
                list.Add(*(nint*)(controller + NativeFields.AnimationController_Control));
            Walk(*(nint*)(node + NativeFields.StdTreeNode_Right), depth + 1);
        }
        Walk(*(nint*)(head + 8), 0);
        return list;
    }

    // hkaDefaultAnimationControl (stock Havok 2014): +0x60 forward wraps, +0x68
    // MaxCycles (≤ 0 loops forever), +0x9C/+0xA0 crop start/end in seconds. Once
    // the wraps reach MaxCycles, Havok parks the control at its end with speed 0.
    private static uint Wraps(nint control) => *(uint*)(control + NativeFields.hkaDefaultAnimationControl_OverflowCount);
    private static int MaxCycles(nint control) => *(int*)(control + NativeFields.hkaDefaultAnimationControl_MaxCycles);

    private static bool HeldByHavok(nint control) =>
        MaxCycles(control) > 0 && Wraps(control) >= (uint)MaxCycles(control);

    /// <summary>The length one cycle plays, in seconds: the animation minus its crops.</summary>
    private static float CycleLength(nint control)
    {
        var binding = ((FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl*)control)
            ->hkaAnimationControl.Binding.ptr;
        if (binding == null || binding->Animation.ptr == null)
            return 0f;
        return binding->Animation.ptr->Duration - *(float*)(control + NativeFields.hkaDefaultAnimationControl_CropEnd) - *(float*)(control + NativeFields.hkaDefaultAnimationControl_CropStart);
    }

    /// <summary>The first C009 control of a clamped root Havok still plays: its
    /// time and its span, in seconds (the reading a clamped layer scrubs by). A
    /// looping control spans one cycle, a finite one all its cycles; with every
    /// control parked, the end.</summary>
    public (float Time, float Duration)? ClampedReading(nint root)
    {
        foreach (var clip in Collect(root).Havok.Keys)
            foreach (var control in ClipControls(clip))
            {
                float length = CycleLength(control);
                if (!(length > 0f) || HeldByHavok(control))
                    continue;
                float local = *(float*)(control + NativeFields.hkaDefaultAnimationControl_LocalTime);
                int max = MaxCycles(control);
                return max > 0 ? (Wraps(control) * length + local, max * length) : (local, length);
            }
        // Only parked finite controls: the layer stands at its end. Havok counts the
        // last wrap when it parks one (+0x60 = max, LocalTime = its length).
        foreach (var clip in Collect(root).Havok.Keys)
            foreach (var control in ClipControls(clip))
                if (CycleLength(control) is var length && length > 0f && HeldByHavok(control))
                    return (MaxCycles(control) * length, MaxCycles(control) * length);
        return null;
    }

    /// <summary>Seeks a clamped root by its controls (§15.3): a looping control to
    /// <paramref name="seconds"/> wrapped into its cycle; a finite one to that cycle
    /// and its time in it, or parked at its end where its cycles have run out.</summary>
    public int SeekClampedControls(nint root, float seconds)
    {
        int moved = 0;
        foreach (var clip in Collect(root).Havok.Keys)
            foreach (var control in ClipControls(clip))
            {
                float length = CycleLength(control);
                if (!(length > 0f))
                    continue;
                ref float local = ref *(float*)(control + NativeFields.hkaDefaultAnimationControl_LocalTime);
                int max = MaxCycles(control);
                if (max <= 0)
                {
                    float t = seconds % length;
                    local = t < 0f ? t + length : t;
                }
                else
                {
                    // The wraps are written too, so a later resume counts on from here.
                    int cycles = (int)MathF.Floor(Math.Max(seconds, 0f) / length);
                    if (cycles >= max)
                    {
                        local = length;
                        *(uint*)(control + NativeFields.hkaDefaultAnimationControl_OverflowCount) = (uint)max;
                    }
                    else
                    {
                        bool parked = HeldByHavok(control);
                        local = Math.Max(seconds, 0f) - cycles * length;
                        *(uint*)(control + NativeFields.hkaDefaultAnimationControl_OverflowCount) = (uint)cycles;
                        // Havok zeroed a parked control's speed. Slot 35 (speed changed)
                        // gives it the clip's speed back while playing; paused, it does nothing.
                        if (parked)
                            ((delegate* unmanaged<nint, void>)(*(nint*)(*(nint*)clip + 35 * 8)))(clip);
                    }
                }
                moved++;
            }
        return moved;
    }

    /// <summary>Status/aura effects (VfxContainer chara+0x19A0, 14 entries) and
    /// tethers (chara+0x1A30, 2 × 0x18) at a speed: the game pushes them only on a
    /// speed change, and one spawned after a pause starts at 1 (§10.2).</summary>
    public void SetStatusVfxSpeed(nint character, float speed)
    {
        if (_n.SetVfxDataSpeed == null)
            return;
        for (int i = 0; i < 14; i++)
        {
            nint vd = *(nint*)(character + NativeFields.Character_StatusVfx + i * 8);
            if (vd != 0)
                _n.SetVfxDataSpeed(vd, speed);
        }
        for (int j = 0; j < 2; j++)
        {
            nint vd = *(nint*)(character + NativeFields.Character_Tethers + j * 0x18);
            if (vd != 0)
                _n.SetVfxDataSpeed(vd, speed);
        }
    }

    /// <summary>
    /// The manual loop wrap: event 9 through each non-C010 clip's own
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
        uint fade = *(uint*)(root + NativeFields.SchedulerTimeline_StopFadeFrames);
        *(uint*)(root + NativeFields.SchedulerTimeline_StopFadeFrames) = 0;
        try { StopAll(root); }
        finally { *(uint*)(root + NativeFields.SchedulerTimeline_StopFadeFrames) = fade; }
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
            nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
            nint holder = instance != 0 ? *(nint*)(instance + NativeFields.ClipAnimation_Holder) : 0;
            if (holder != 0)
                holders.Add(holder);
        }
        nint playInfo = PlayInfo(root);
        bool viaPlayInfo = playInfo != 0 && *(int*)(playInfo + NativeFields.TimelinePlayInfo_BlendIn) >= 0;
        LastBlendPath = viaPlayInfo ? "play info +0x38" : "+0x21C";
        int fade = *(int*)(root + NativeFields.SchedulerTimeline_StopFadeFrames);
        int blend = viaPlayInfo ? *(int*)(playInfo + NativeFields.TimelinePlayInfo_BlendIn) : *(int*)(root + NativeFields.SchedulerTimeline_BlendOverride);
        byte flags = *(byte*)(root + NativeFields.SchedulerTimeline_Flags272);
        // The stopped holders' own fade-to-empty multiplies into the blend node's
        // crossfade (old weight (1 − x)², the sum dipped to ~0.77 mid-seam); 0 drops
        // them at once (the body fell to the new control's ~0 weight: a bind-pose
        // frame). A fade far longer than the blend leaves only the crossfade, the
        // sum stays 1 as at the game's own seam, and the node releases them when
        // the blend completes (measured: removed the frame after it). Only C009s:
        // the VFX clips' stop reads the same field, and 1000 left the old emitters
        // fading for ~33 s (the stuck bubbles). They stop with 0, as in a scrub.
        if (viaPlayInfo)
            *(int*)(playInfo + NativeFields.TimelinePlayInfo_BlendIn) = frames;
        else
        {
            *(int*)(root + NativeFields.SchedulerTimeline_BlendOverride) = frames;
            *(byte*)(root + NativeFields.SchedulerTimeline_Flags272) = (byte)(flags | 2);
        }
        // A NoBlendStart left set (a seek's banner chunk sets it, UpdateBanner clears it
        // only at a chunk's end) makes the holder insert take a blend of 0: no
        // crossfade, and for a pose-fade animation no fade either.
        nint actorControl = _n.GetActorControl(root);
        bool noBlendWasSet = actorControl != 0 && _l.IsActorControl(actorControl) && *(byte*)(actorControl + NativeFields.ActorControl_NoBlendStart) != 0;
        if (noBlendWasSet)
            *(byte*)(actorControl + NativeFields.ActorControl_NoBlendStart) = 0;
        try
        {
            StopAll(root, clip => *(int*)(root + NativeFields.SchedulerTimeline_StopFadeFrames) =
                _l.Classify(clip) == SeekClipKind.HavokAnimation ? OldHolderFadeFrames : 0);
            foreach (var holder in holders)
                *(uint*)(holder + NativeFields.AnimationHolder_CreatorInstanceId) = ~*(uint*)(holder + NativeFields.AnimationHolder_CreatorInstanceId);
            Rewind(root, tree);
            int created = 0;
            foreach (var clip in tree.Havok.Keys)
            {
                nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
                nint holder = instance != 0 ? *(nint*)(instance + NativeFields.ClipAnimation_Holder) : 0;
                if (holder != 0 && !holders.Contains(holder))
                    created++;
            }
            LastBlendPath += $"; NoBlendStart was {(noBlendWasSet ? "set" : "clear")}, {created} of {holders.Count} holders new";
        }
        finally
        {
            *(int*)(root + NativeFields.SchedulerTimeline_StopFadeFrames) = fade;
            if (viaPlayInfo)
                *(int*)(playInfo + NativeFields.TimelinePlayInfo_BlendIn) = blend;
            else
            {
                *(int*)(root + NativeFields.SchedulerTimeline_BlendOverride) = blend;
                // Rewind rewrites bit 7 of +0x272; only bit 1 goes back.
                *(byte*)(root + NativeFields.SchedulerTimeline_Flags272) = (byte)((*(byte*)(root + NativeFields.SchedulerTimeline_Flags272) & ~2) | (flags & 2));
            }
        }
    }

    private const int OldHolderFadeFrames = 1000;

    /// <summary>The root's play info (vfunc 0x118: root+0x110 while +0x271 bit 6), or 0.</summary>
    public static nint PlayInfo(nint root)
    {
        var playInfoOf = (delegate* unmanaged<nint, nint>)(*(nint*)(*(nint*)root + NativeFields.SchedulerTimeline_VfPlayInfo));
        return playInfoOf(root);
    }

    /// <summary>No pose: a conditional variant its owner doesn't play (started with
    /// anim id 0, inst+0x40 == 0, whose 0x98 returns its full length for ever), or
    /// a refused holder. The game's finalization doesn't wait on them.</summary>
    private static bool NoPose(nint clip)
    {
        nint instance = *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject);
        return instance != 0 && (*(int*)(instance + NativeFields.ClipAnimation_AnimId) == 0 ||
            (*(nint*)(instance + NativeFields.ClipAnimation_Holder) == 0 && *(byte*)(instance + NativeFields.ClipAnimation_CreateState) == 1));
    }

    /// <summary>
    /// The frames the root's C009s have left, using the same remaining-length query as the
    /// sequencer's pre-end notification. Calling that notification routine directly
    /// could send an end event. This takes the maximum C009 vfunc 0x98 result
    /// (the query can initialize the cached length), skipping clips marked +0x10 bit 7; 0 once the root is
    /// torn down (+0x273 bit 0); −1 without C009s. In the clip's own units: a
    /// stretched clip (an expression's 1-frame face at 1/150) counts down slower
    /// than the timeline, so callers measure its rate rather than assume it.
    /// </summary>
    public float Remaining(nint root)
    {
        if (TimelineState.Of(root).Dead)
            return 0f;
        float remaining = -1f;
        foreach (var clip in Collect(root).Havok.Keys)
        {
            if ((*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x80) != 0)
                continue;
            if (NoPose(clip))
                continue;
            var remainingOf = (delegate* unmanaged<nint, float>)(*(nint*)(*(nint*)clip + NativeFields.HavokAnimationClip_AnimObject));
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
        nint actor = *(nint*)(timeline + NativeFields.SchedulerTimeline_Actors);
        for (int a = 0; actor != 0 && a < 32; a++, actor = *(nint*)(actor + NativeFields.TmbActor_Next))
        {
            nint tracks = *(nint*)(actor + NativeFields.TmbActor_Tracks);
            int trackCount = *(ushort*)(actor + NativeFields.TmbActor_TrackCount);
            for (int t = 0; tracks != 0 && t < trackCount && t < 64; t++)
            {
                nint track = *(nint*)(tracks + t * 8);
                if (track == 0)
                    continue;
                any = true;
                byte trackFlags = *(byte*)(track + NativeFields.TmbTrack_Flags);
                if ((trackFlags & 0x80) != 0)
                    continue;
                if ((trackFlags & 1) == 0)
                    return false;
                nint clips = *(nint*)(track + NativeFields.TmbTrack_Clips);
                int clipCount = *(ushort*)(track + NativeFields.TmbTrack_ClipCount);
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
        byte flags = *(byte*)(clip + NativeFields.SchedulerClip_State);
        if ((flags & 0x80) != 0)
            return true;
        switch (_l.Classify(clip))
        {
            case SeekClipKind.HavokAnimation:
                return false;
            case SeekClipKind.ChildTimeline:
                nint node = *(nint*)(clip + NativeFields.TimelineClip_SkipNode);
                if (node != 0 && ((*(uint*)(node + NativeFields.TimelineClipSkipNode_Flags84) >> 1) & 1) != 0)
                    return true;
                if (*(byte*)(clip + NativeFields.TimelineClip_Skip) != 0)
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
        // The C010s go back to never-started so the step starts each again as it
        // passes, by the game's own reset (Mac, animation-system.md §15.3). A
        // C010's own Stop is never used: it finalizes the child, and a re-armed C010
        // whose child reads finalized (child +0x10 bit 7, checked first) ends on the
        // spot. Which ones:
        //  - an ENDED one (+0x10 bit 4 clear, bits 6/7 set: 0xE1), anywhere;
        //  - an ACTIVE one nested below the root's own C010 (each face clip's range
        //    C010). Only the root runs the finalization that could end the row, so the
        //    root's own active C010s are left as they are.
        // For each, deepest first (a parent's event 9 stops a C010 only if started):
        //  1. a finished child (+0x7C bit 6) is unfinished: its event 9 returns early;
        //  2. the child gets event 9 (ProcessAll): its own, its actors' and tracks'
        //     +0x10 &= 3 and +0x11 &= 0xE2, clock 0, +0x7C &= 0xBE, and every clip
        //     Stopped (2) if started and reset. +0x58 (loop mode) is untouched;
        //  3. the C010 gets Event10(clip, 0) (slot 47): its start/duration re-derived,
        //     +0x10 &= 3, +0x11 &= 0xE2, the start credit +0x7C and the pending +0x8C
        //     cleared with the parent track's and actor's pending counts.
        // The root's own active C010s are re-started by the frame-0 activation below
        // (pending-2 path), which pre-pays one more start credit (+0x7C 1 → 2; natural
        // play keeps 1): theirs goes back afterwards.
        var credits = new List<(nint Clip, uint Credit)>();
        foreach (var (c010, _) in tree.C010s)
            if (!tree.NestedC010s.Contains(c010) && (*(byte*)(c010 + NativeFields.SchedulerClip_State) & 0x10) != 0)
                credits.Add((c010, *(uint*)(c010 + NativeFields.SchedulerClip_StartCredit)));
        for (int i = tree.C010s.Count - 1; i >= 0; i--)
        {
            var (c010, child) = tree.C010s[i];
            byte state = *(byte*)(c010 + NativeFields.SchedulerClip_State);
            bool active = (state & 0x10) != 0;
            bool ended = (state & 0xC0) != 0;
            if (active ? !tree.NestedC010s.Contains(c010) : !ended)
            {
                // Never started: only a finished child to undo.
                if (active || (*(byte*)(child + NativeFields.SchedulerTimeline_ClockFlags) & 0x40) == 0)
                    continue;
                *(byte*)(child + NativeFields.SchedulerTimeline_ClockFlags) &= 0xBF;
                _n.ProcessAll(child, 9, _l.DefaultPayload);
                continue;
            }
            *(byte*)(child + NativeFields.SchedulerTimeline_ClockFlags) &= 0xBF;
            _n.ProcessAll(child, 9, _l.DefaultPayload);
            var event10 = (delegate* unmanaged<nint, int, void>)(*(nint*)(*(nint*)c010 + 47 * 8));
            event10(c010, 0);
        }

        _n.SetCurrentTime(root, 0f);
        _n.ActivateClips(root, 4);
        bool handled = _n.ProcessPending(root, 0x11) != 0;
        *(byte*)(root + NativeFields.SchedulerTimeline_Flags272) = (byte)((handled ? 0 : 0x80) | (*(byte*)(root + NativeFields.SchedulerTimeline_Flags272) & 0x7F));
        if (handled)
            _n.ProcessAll(root, 0x12, _l.DefaultPayload);
        foreach (var (clip, credit) in credits)
            *(uint*)(clip + NativeFields.SchedulerClip_StartCredit) = credit;
    }

}
