using System.Collections.Generic;

namespace Poser.Game.Animation;

/// <summary>
/// The frames after one seek (#533). Clips that started inside the step fail the
/// sync guard for a frame or two, a prop timeline may still be loading, and only
/// once the deferred frames have run can the VFX handles from before the seek be
/// checked for orphans. Pumped once per framework tick until it reports done.
/// </summary>
internal sealed unsafe class TimelineSeekWork
{
    /// <summary>Deferred frames after a seek: clips still pending get their one
    /// sync as soon as they pass the guard, props are seeked once loaded, then the
    /// VFX orphan check runs. Only pending clips are touched: a blanket per-frame
    /// re-sync opened a speed window every frame, and a C009's speed restore lands
    /// one update later, so the controls ran a frame at 1.0 and snapped back.</summary>
    public const int ResyncFrames = 20;
    // A synced clip's in-tick post-start re-sync is cancelled (+0xCC bit 0), so the
    // seek frame already renders right; no blanket re-sync afterwards.
    private const int RestartedResyncFrames = 0;

    internal sealed class Prop
    {
        public readonly nint Clip;
        public readonly float Start;
        public nint Timeline;
        public bool Seeked;
        public bool Fired;
        public readonly HashSet<nint> Synced = new();
        public Prop(nint clip, float start) { Clip = clip; Start = start; }
    }

    public nint Character { get; private set; }
    public nint Root { get; private set; }
    public int Frame { get; private set; }
    /// <summary>The body's speed before the seek's window: what Poser's pause
    /// (0) or the game left it at, and what a re-born prop must run at.</summary>
    public float OwnerSpeed;
    public readonly HashSet<nint> PendingHavok = new();
    public readonly List<Prop> Props = new();
    public HashSet<ulong> VfxBefore { get; private set; } = new();
    /// <summary>A reload restore: re-fire C031s whose start gave up while the
    /// new weapon object was loading, and keep pumping until they are seeked.</summary>
    public bool AllowFire;
    /// <summary>Upper bound for waiting on a prop's weapon object.</summary>
    public const int PropWaitFrames = 300;
    public string? Log;

    public void Begin(nint character, nint root, HashSet<ulong> vfxBefore)
    {
        Character = character;
        Root = root;
        VfxBefore = vfxBefore;
        Frame = 0;
        PendingHavok.Clear();
        Props.Clear();
    }

    /// <summary>Seeks each prop timeline that exists and is loaded to
    /// root clock − absolute C031 start; one still loading waits for a later frame.</summary>
    public void PumpProps(TimelineSeekEngine engine)
    {
        float body = TimelineState.Of(Root).Clock;
        foreach (var prop in Props)
        {
            if (prop.Seeked)
            {
                if (Live(engine, prop) is var live and not 0 && engine.PropControl(live) is var control and not 0)
                    engine.SyncPropControls(live, control, prop.Synced);
                continue;
            }
            float target = body - prop.Start;
            if (target < 0f)
            {
                prop.Seeked = true; // before its start: the clip fires by itself
                continue;
            }
            nint weapon = TimelineSeekEngine.PropWeapon(Character, prop.Clip);
            nint timeline = TimelineSeekEngine.CurrentPropTimeline(
                engine.PropGroup(weapon), TimelineSeekEngine.PropTimelineId(prop.Clip));
            if (timeline == 0 && AllowFire && !prop.Fired && engine.FireProp(Character, prop.Clip))
                prop.Fired = true;
            if (timeline == 0 || !TimelineState.Of(timeline).Loaded)
                continue;
            prop.Timeline = timeline;
            prop.Seeked = true;
            if (*(float*)(timeline + 0x34) > target + 0.5f)
                continue; // ahead: never step a prop backward
            engine.SeekProp(timeline, target, OwnerSpeed, prop.Synced);
        }
    }

    /// <summary>
    /// A seeked prop's timeline while it is still its group's current one; 0 once the
    /// game replaced or released it (an actor reset stops props while the body's
    /// timeline stays), which leaves the stored pointer dangling.
    /// </summary>
    private nint Live(TimelineSeekEngine engine, Prop prop)
    {
        if (prop.Timeline == 0)
            return 0;
        nint current = TimelineSeekEngine.CurrentPropTimeline(
            engine.PropGroup(TimelineSeekEngine.PropWeapon(Character, prop.Clip)),
            TimelineSeekEngine.PropTimelineId(prop.Clip));
        if (current != prop.Timeline)
            prop.Timeline = 0;
        return prop.Timeline;
    }

    /// <summary>Paused owner: the body tree's and the seeked props' effects go
    /// back to 0 after every speed window (see TimelineSeekEngine.SetVfxSpeeds).</summary>
    public void FreezeVfx(TimelineSeekEngine engine)
    {
        engine.SetVfxSpeeds(Root, 0f);
        foreach (var prop in Props)
            if (Live(engine, prop) is var live and not 0)
                engine.SetVfxSpeeds(live, 0f);
    }

    /// <summary>One deferred frame; false once finished. Stops early if the slot
    /// timeline was replaced (another play, a redraw).</summary>
    public bool Tick(TimelineSeekEngine engine, nint currentRoot)
    {
        Frame++;
        if (currentRoot != Root)
            return false;
        var tree = engine.Collect(Root);
        if (Frame <= RestartedResyncFrames)
            engine.SyncHavok(Root, tree.Havok.Keys, PendingHavok);
        else if (Frame <= ResyncFrames && PendingHavok.Count > 0)
        {
            // Only clips still in the live tree: a pending pointer may be gone.
            var live = new List<nint>();
            foreach (var clip in tree.Havok.Keys)
                if (PendingHavok.Contains(clip))
                    live.Add(clip);
            PendingHavok.IntersectWith(tree.Havok.Keys);
            if (live.Count > 0)
                engine.SyncHavok(Root, live, PendingHavok);
        }
        PumpProps(engine);
        if (OwnerSpeed == 0f)
            FreezeVfx(engine);
        if (Frame < ResyncFrames)
            return true;
        if (AllowFire && Frame < PropWaitFrames && Props.Exists(p => !p.Seeked))
            return true;
        int released = engine.ReleaseOrphans(VfxBefore, engine.VfxHandles(tree));
        if (released > 0)
            Log = $"released {released} orphaned VFX instance(s)";
        return false;
    }
}
