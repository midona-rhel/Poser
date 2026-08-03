using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI;
using Ui = Poser.UI.Crystarium;

namespace Crystarium.Capture;

/// <summary>
/// The ONE frame/context driver behind every behavior suite. A suite owns
/// the hidden form, the D3D device and the shared font atlas exactly once;
/// each case then runs in its OWN ImGui context over that atlas, so no
/// widget, timing or interaction state survives from one case into the
/// next while the expensive atlas build still happens only once.
///
/// A case is data plus a small per-frame lambda that draws and accumulates
/// into the CALLER's locals — which is why <see cref="Case"/> returns
/// nothing, and why one driver serves tallies, sizes, motion values and
/// focus alike without a result type per suite.
/// </summary>
internal sealed class BehaviorHost
{
    private readonly ImFontAtlasPtr atlas;
    /// <summary>Makes the suite's root context current again. Typed as a
    /// callback so the host never has to name the binding's context handle.
    /// </summary>
    private readonly Action restoreRoot;
    private readonly List<(string Name, bool Ok, string Detail)> results =
        new();

    internal BehaviorHost(ImFontAtlasPtr atlas, Action restoreRoot)
    {
        this.atlas = atlas;
        this.restoreRoot = restoreRoot;
    }

    internal void Check(string name, bool ok, string detail) =>
        results.Add((name, ok, detail));

    /// <summary>A case whose whole report is its problem list: empty
    /// passes.</summary>
    internal void Check(string name, object problems) =>
        Check(name, problems.ToString()!.Length == 0, problems.ToString()!);

    internal void Expect(string name, object actual, object expected) =>
        Check(name, actual.Equals(expected), $"{actual}, want {expected}");

    /// <summary>One PASS/FAIL line per case on stdout.</summary>
    internal int Each()
    {
        foreach (var (name, ok, detail) in results)
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
        return results.TrueForAll(result => result.Ok) ? 0 : 1;
    }

    /// <summary>One aggregate line when everything passed, one stderr line
    /// per failure otherwise.</summary>
    internal int Summary(string passLine)
    {
        var failed = results.FindAll(result => !result.Ok);
        foreach (var (name, _, detail) in failed)
            Console.Error.WriteLine($"FAIL {name}: {detail}");
        if (failed.Count > 0)
            return 1;
        Console.WriteLine(passLine);
        return 0;
    }

    /// <summary>A case scripted frame by frame, for the checks that need a
    /// real frame counter but no input at all.</summary>
    internal void Case(Vector2 canvas, params Action[] script) =>
        Case(canvas, script.Length, frame => script[frame]());

    /// <summary>One case: a fresh context over the shared atlas driven for
    /// <paramref name="frames"/> REAL frames, feeding whatever pointer,
    /// mouse and key events each frame asks for. The body draws inside a
    /// borderless full-canvas window.</summary>
    internal unsafe void Case(
        Vector2 canvas,
        int frames,
        Action<int> body,
        Func<int, Vector2>? pointer = null,
        Func<int, (bool HasEvent, bool Down)>? mouse = null,
        Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
        Func<int, float>? wheel = null,
        Func<int, string?>? text = null)
    {
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = 1f;
            io.DeltaTime = 1f / 60f;
            io.DisplaySize = canvas;
            io.IniFilename = null;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();
            for (int frame = 0; frame < frames; frame++)
            {
                if (pointer?.Invoke(frame) is { } at)
                    io.AddMousePosEvent(at.X, at.Y);
                if (mouse?.Invoke(frame) is { HasEvent: true } click)
                    io.AddMouseButtonEvent(0, click.Down);
                if (key?.Invoke(frame) is { HasEvent: true } stroke)
                    io.AddKeyEvent(stroke.Key, stroke.Down);
                // Mirrors the capture loop's wheel hook, in the same units
                // (ImGui notches). ImGui drops a zero wheel event before it
                // queues it, so an unscripted case pays nothing.
                if (wheel?.Invoke(frame) is { } notches)
                    io.AddMouseWheelEvent(0f, notches);
                // Typed characters, for the one thing a retained tree cannot
                // own: a native text field. They only land where ImGui has an
                // active InputText, so a case that wants them has to focus the
                // field first, exactly as a user does.
                if (text?.Invoke(frame) is { Length: > 0 } typed)
                {
                    foreach (char typedChar in typed)
                        io.AddInputCharacter(typedChar);
                }
                ImGui.NewFrame();
                Interactive.BeginFrame();
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(canvas);
                ImGui.PushStyleVar(
                    ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.Begin(
                    "##behavior",
                    ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoBackground);
                ImGui.PopStyleVar();
                body(frame);
                ImGui.End();
                Interactive.EndFrame();
                ImGui.Render();
            }
        }
        finally
        {
            // Mirror the capture host: make the SUITE's root current before
            // destroying the case's context. DestroyContext clears the
            // current context when the context it destroys is the current
            // one, and every measurement seam reaches for ImGui — so a suite
            // that derived geometry between cases would otherwise dereference
            // a null context and hang the process rather than throw.
            restoreRoot();
            ImGui.DestroyContext(context);
        }
    }
}

/// <summary>
/// The suites themselves. The pixel fixtures cannot reach any of this — a
/// control under an open surface, a drag covered mid-flight, a transition
/// with no duration, a focus handover — so every case drives real input
/// frames and asserts the outcome.
/// </summary>
internal static class BehaviorSuites
{
    private const string OccluderId = "##kernel-occluder";
    private const string TargetId = "##kernel-target";
    private static readonly Vector2 Canvas = new(160, 80);
    // The reserved 28x28 control sits at (24,24); this point is inside it.
    private static readonly Vector2 Inside = new(38, 38);
    private static readonly Vector2 Offscreen = new(-1000, -1000);

    /// <summary>What the exclusive surface does on a frame: absent,
    /// claimed with NO geometry yet (the opening frame), or claimed and
    /// registered over its rectangle.</summary>
    private enum SurfaceState { None, ClaimOnly, Registered }

    private sealed class ReserveTally
    {
        public int Clicked, Activated, DragBegan, DragEnded;

        public override string ToString() =>
            $"clicked={Clicked} activated={Activated} " +
            $"began={DragBegan} ended={DragEnded}";
    }

    /// <summary>A case's problem list; empty means it passed.</summary>
    private sealed class Probe
    {
        private readonly List<string> problems = new();

        public void Want(string name, object actual, object expected)
        {
            if (!actual.Equals(expected))
                problems.Add($"{name}: {actual}, want {expected}");
        }

        public void Fault(string problem) => problems.Add(problem);

        public override string ToString() => string.Join("; ", problems);
    }

    private static unsafe int Suite(
        string title, int width, int height, Func<BehaviorHost, int> cases)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new CaptureForm
        {
            Text = title,
            ClientSize = new Size(width, height),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();
        using var renderer = new Dx11Renderer();
        renderer.Initialize(form.Handle, width, height);
        Ui.IconTextureUploader = renderer.CreateIconTexture;
        var rootContext = ImGui.CreateContext();
        try
        {
            var rootIo = ImGui.GetIO();
            rootIo.IniFilename = null;
            Ui.UseTheme(Theme.PictoDark);
            using var fonts = new StandaloneFontAtlas(renderer);
            FontRegistry.Register(fonts);
            fonts.BuildFontsImmediately();
            if (!FontRegistry.Ready)
                throw new InvalidOperationException(
                    $"Font atlas is not ready: {FontRegistry.LastError}");
            return cases(new BehaviorHost(
                ImGui.GetIO().Fonts,
                () => ImGui.SetCurrentContext(rootContext)));
        }
        finally
        {
            // Before the renderer goes: the baked icon handles are its.
            Ui.IconTextureUploader = null;
            FontRegistry.Dispose();
            ImGui.DestroyContext(rootContext);
        }
    }

    // ---- Icon Button: the momentary action contract -------------------

    internal static int IconButton() =>
        Suite("Crystarium icon-button behavior", 500, 80, IconButtonCases);

    private static int IconButtonCases(BehaviorHost host)
    {
        Func<int, Vector2> on = _ => Inside;
        Func<int, Vector2> away = _ => Offscreen;
        host.Expect("release-inside", Drive(on, PressAt(5, 7)).Hits, 1);
        host.Expect(
            "drag-release-outside",
            Drive(f => f < 6 ? Inside : new Vector2(110, 70), PressAt(5, 7))
                .Hits,
            0);
        host.Expect(
            "enter", Drive(away, key: TabThen(ImGuiKey.Enter)).Hits, 1);
        host.Expect(
            "space", Drive(away, key: TabThen(ImGuiKey.Space)).Hits, 1);
        host.Expect(
            "disabled", Drive(on, PressAt(5, 7), disabled: true).Hits, 0);
        // Size is a layout question, so it is asked with no input at all.
        host.Expect(
            "default-size", Drive(away, canvasWidth: 500).Size, "28x28");
        host.Expect(
            "explicit-size",
            Drive(away, style: ControlStyle.Square(36f), canvasWidth: 500)
                .Size,
            "36x36");
        return host.Summary(
            "PASS release-inside=1 drag-release-outside=0 " +
            "enter=1 space=1 disabled=0 default=28x28 explicit=36x36");

        (int Hits, string Size) Drive(
            Func<int, Vector2> pointer,
            Func<int, (bool HasEvent, bool Down)>? mouse = null,
            Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
            bool disabled = false,
            ControlStyle style = default,
            int canvasWidth = 120)
        {
            int activations = 0;
            Vector2 size = default;
            host.Case(new Vector2(canvasWidth, 80), 12, _ =>
            {
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                Ui.IconButton(
                    TablerIcon.Settings,
                    () => activations++,
                    style,
                    disabled,
                    id: "##behavior-icon-button");
                size = ImGui.GetItemRectSize();
            }, pointer, mouse, key);
            return (activations, $"{size.X}x{size.Y}");
        }
    }

    // ---- Interaction kernel -------------------------------------------

    internal static int Kernel() =>
        Suite("Crystarium kernel behavior", 160, 80, KernelCases);

    private static int KernelCases(BehaviorHost host)
    {
        Func<int, Vector2> on = _ => Inside;
        Func<int, Vector2> away = _ => Offscreen;

        // (a) A pointer press landing under a higher surface reports
        //     neither Clicked nor Activated; the same sequence with no
        //     surface proves the press itself is real.
        Gated(
            "occluded-pointer",
            (free, under) => free.Clicked == 1 && free.Activated == 1
                && under.Clicked == 0 && under.Activated == 0,
            on,
            mouse: PressAt(5, 7));

        // (b) Keyboard focus with the pointer parked offscreen, so the
        //     POINTER gate cannot be what suppresses activation: a surface
        //     COVERING the control is.
        Gated(
            "keyboard-overlap",
            (free, under) => free.Activated == 2 && under.Activated == 0,
            away,
            key: TabEnterSpace);

        // (c) A press swallowed by a surface must not open a drag — and
        //     must not leave a dangling release behind either.
        Gated(
            "occluded-drag-begin",
            (free, under) => free.DragBegan == 1 && free.DragEnded == 1
                && under.DragBegan == 0 && under.DragEnded == 0,
            frame => frame < 6 ? Inside : Inside + new Vector2(10, 4),
            mouse: PressAt(4, 8));

        // (d) Keyboard ownership is NOT geometric: a settled exclusive
        //     surface entirely to the right of the 24..52 control still
        //     owns the keyboard, so Enter at frame 6 must not activate —
        //     while Space at frame 10, after the release, must.
        var aside = Reserve(
            host, away, key: TabEnterSpace,
            surface: frame =>
                frame <= 8 ? SurfaceState.Registered : SurfaceState.None,
            surfaceMin: new Vector2(100, 0),
            surfaceMax: new Vector2(160, 80));
        host.Check(
            "keyboard-exclusive-nonoverlap",
            aside.Activated == 1,
            $"{aside} (want activated=1: Enter blocked, Space accepted "
            + "after release)");

        // (e) The claim frame itself: the surface has registered no
        //     rectangle yet, so only the opening barrier can answer.
        var barrier = Reserve(
            host, away, key: TabEnterSpace,
            surface: frame => frame is 6 or 10
                ? SurfaceState.ClaimOnly
                : SurfaceState.None);
        host.Check(
            "keyboard-claim-barrier", barrier.Activated == 0, $"{barrier}");

        // (e2) Only the chain TAIL owns the keyboard: a control inside the
        //      PARENT surface loses Enter while a child is open, and takes
        //      it back the moment the child releases.
        host.Check("keyboard-nested-child", NestedKeyboard(host));

        // (f) Ownership, not the current occlusion state, is what pairs
        //     the drag edges: a surface opening over a HELD control must
        //     not swallow the release.
        var midDrag = Reserve(
            host, on, PressAt(4, 8),
            surface: frame =>
                frame >= 6 ? SurfaceState.Registered : SurfaceState.None);
        host.Check(
            "drag-end-exactly-once",
            midDrag.DragBegan == 1 && midDrag.DragEnded == 1,
            $"{midDrag}");

        // (g) Motion's contract, its zero-duration snap, and its refusal
        //     to advance a clock it cannot trust.
        host.Check("motion-contract", MotionContract(host));
        host.Check("motion-zero-duration", MotionZeroDuration(host));
        host.Check("motion-frame-reset", MotionFrameReset(host));

        // (h) Clearing is an edit of the field the user is in, so the
        //     field takes focus back on the frame it next submits — and
        //     only on that frame.
        host.Check("textinput-clear-focus", ClearFocus(host, gap: 0));
        host.Check("refocus-expiry", ClearFocus(host, gap: 4));

        // (i) A row carrying two overlapping targets routes ONE outcome
        //     per gesture: the arrow and the row body can never both fire.
        host.Check("sidebar-expander-routing", SidebarRouting(host));

        return host.Each();

        // The occlusion-shaped cases all read the same way: one input
        // sequence run with and without a surface over the control.
        void Gated(
            string name,
            Func<ReserveTally, ReserveTally, bool> ok,
            Func<int, Vector2> pointer,
            Func<int, (bool HasEvent, bool Down)>? mouse = null,
            Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null)
        {
            var free = Reserve(host, pointer, mouse, key);
            var under = Reserve(
                host, pointer, mouse, key, _ => SurfaceState.Registered);
            host.Check(
                name, ok(free, under), $"blocked[{under}] baseline[{free}]");
        }
    }

    private static Func<int, (bool HasEvent, bool Down)> PressAt(
        int down, int up) =>
        frame => frame == down
            ? (true, true)
            : frame == up ? (true, false) : default;

    private static Func<int, (bool, ImGuiKey, bool)> TabThen(ImGuiKey key) =>
        frame => frame switch
        {
            2 => (true, ImGuiKey.Tab, true),
            3 => (true, ImGuiKey.Tab, false),
            6 => (true, key, true),
            7 => (true, key, false),
            _ => default,
        };

    private static (bool, ImGuiKey, bool) TabEnterSpace(int frame) =>
        frame switch
        {
            10 => (true, ImGuiKey.Space, true),
            11 => (true, ImGuiKey.Space, false),
            _ => TabThen(ImGuiKey.Enter)(frame),
        };

    /// <summary>One reserved control, optionally under an exclusive
    /// surface, driven by real pointer/key frames.</summary>
    private static ReserveTally Reserve(
        BehaviorHost host,
        Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)>? mouse = null,
        Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
        Func<int, SurfaceState>? surface = null,
        Vector2 surfaceMin = default,
        Vector2? surfaceMax = null)
    {
        var tally = new ReserveTally();
        var max = surfaceMax ?? Canvas;
        // Interaction ownership is process-wide state; no case may inherit
        // a chain link from the one before it.
        Interactive.ReleaseExclusive(OccluderId);
        try
        {
            host.Case(Canvas, 16, frame =>
            {
                // Claimed BEFORE the control, so the surface is visible to
                // Reserve on the very frame it appears.
                var state = surface?.Invoke(frame) ?? SurfaceState.None;
                if (state == SurfaceState.None)
                    Interactive.ReleaseExclusive(OccluderId);
                else if (!Interactive.OwnsExclusive(OccluderId))
                    Interactive.ClaimExclusive(OccluderId);
                if (state == SurfaceState.Registered)
                    Interactive.EndOwner(Interactive.BeginOwner(
                        OccluderId,
                        InteractionLayer.Popup,
                        surfaceMin,
                        max));
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                var hit = Interactive.Reserve(
                    TargetId,
                    new Vector2(28f),
                    disabled: false,
                    activateOnSpace: true);
                if (hit.Clicked) tally.Clicked++;
                if (hit.Activated) tally.Activated++;
                if (hit.DragBegan) tally.DragBegan++;
                if (hit.DragEnded) tally.DragEnded++;
            }, pointer, mouse, key);
            return tally;
        }
        finally
        {
            Interactive.ReleaseExclusive(OccluderId);
        }
    }

    /// <summary>A control living INSIDE a parent exclusive surface, with a
    /// nested child claimed over frames 5..8. Enter lands at frame 6 (child
    /// open, so the parent is off the chain tail) and Space at frame 10
    /// (child released, so the parent owns the keyboard again).</summary>
    private static Probe NestedKeyboard(BehaviorHost host)
    {
        const string parent = "##kernel-parent";
        const string child = "##kernel-child";
        int underChild = 0, afterChild = 0;
        Interactive.ReleaseExclusive(parent);
        try
        {
            host.Case(Canvas, 16, frame =>
            {
                // The parent claims from world scope, the child from
                // inside the parent, so the chain nests rather than
                // replaces.
                if (!Interactive.OwnsExclusive(parent))
                    Interactive.ClaimExclusive(parent);
                var owner = Interactive.BeginOwner(
                    parent, InteractionLayer.Modal, Vector2.Zero, Canvas);
                if (frame is >= 5 and <= 8)
                {
                    if (Interactive.OwnsExclusive(child))
                        Interactive.TouchExclusive(child);
                    else
                        Interactive.ClaimExclusive(child);
                }
                else
                {
                    Interactive.ReleaseExclusive(child);
                }
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                var hit = Interactive.Reserve(
                    TargetId,
                    new Vector2(28f),
                    disabled: false,
                    activateOnSpace: true);
                if (hit.Activated)
                {
                    if (frame <= 8) underChild++;
                    else afterChild++;
                }
                Interactive.EndOwner(owner);
            }, _ => Offscreen, key: TabEnterSpace);
        }
        finally
        {
            Interactive.ReleaseExclusive(parent);
        }

        var probe = new Probe();
        probe.Want("enter-under-child", underChild, 0);
        probe.Want("space-after-release", afterChild, 1);
        return probe;
    }

    /// <summary>Motion's channel-set contract. Needs frames only for the
    /// counter and the delta; nothing is drawn.</summary>
    private static Probe MotionContract(BehaviorHost host)
    {
        const uint group = 0x4D0714A1;
        const uint fresh = 0x4D0714A2;
        var transition = Transition.CubicBezier(0.15f, 0.4f, 0f, 0.22f, 1f);
        var probe = new Probe();
        host.Case(
            new Vector2(64),
            () => Threw("seed", group, false, 0, 1),
            () =>
            {
                Threw("dropped-channel", group, true, 0);
                Threw("duplicate-first-call", fresh, true, 0, 0);
            },
            // The throw happens before anything mutates, so the stored
            // group is untouched: the full set goes back in fine.
            () => Threw("readd-after-throw", group, false, 0, 1),
            () => Threw("reordered-channels", group, true, 1, 0),
            () => Threw("extra-channel", group, true, 0, 1, 2),
            () =>
            {
                Threw("duplicate-on-stored-group", group, true, 0, 0);
                Threw("still-usable", group, false, 0, 1);
            });
        return probe;

        void Threw(
            string name, uint id, bool expected, params int[] channels)
        {
            var set = new MotionChannel[channels.Length];
            for (int i = 0; i < channels.Length; i++)
                set[i] = MotionChannel.Number(channels[i], 0f);
            bool threw = false;
            try
            {
                Motion.Toward(id, transition, set.AsSpan());
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            probe.Want(name, threw, expected);
        }
    }

    /// <summary>A zero-duration transition has no clock to run, so it must
    /// arrive on the call that retargets it.</summary>
    private static Probe MotionZeroDuration(BehaviorHost host)
    {
        const uint retargeted = 0x4D0714B1;
        const uint seeded = 0x4D0714B2;
        var instant = new Transition(0f);
        var probe = new Probe();
        host.Case(
            new Vector2(64),
            () =>
            {
                probe.Want("seed", Step(retargeted, 0f), 0f);
                probe.Want("seed-nonzero", Step(seeded, 5f), 5f);
            },
            () => probe.Want("retarget", Step(retargeted, 1f), 1f),
            () => probe.Want("settled", Step(retargeted, 1f), 1f));
        return probe;

        float Step(uint id, float target)
        {
            var set = new[] { MotionChannel.Number(0, target) };
            Motion.Toward(id, instant, set.AsSpan());
            return set[0].Scalar;
        }
    }

    /// <summary>The ramp store outlives the ImGui context, and a recreated
    /// context restarts the frame counter. An entry whose stored frame is
    /// not BELOW the current one therefore carries no usable elapsed time,
    /// so it must reseed exactly as a first sighting does instead of
    /// advancing stale progress.</summary>
    private static Probe MotionFrameReset(BehaviorHost host)
    {
        const uint ramp = 0x4D0714C1;
        float mid = 0f, reset = 0f, duplicate = 0f;
        // Seeds at 0 on its first frame, then ramps forward: five steps of
        // a one-second ramp land well short of the far end.
        host.Case(new Vector2(64), 6,
            frame => mid = Motion.Progress(ramp, frame > 0, 1f));
        // A restarted counter, then the same identity twice in one frame.
        host.Case(new Vector2(64), 1, _ =>
        {
            reset = Motion.Progress(ramp, true, 1f);
            duplicate = Motion.Progress(ramp, false, 1f);
        });

        var probe = new Probe();
        if (mid is <= 0f or >= 1f)
            probe.Fault($"mid-flight: {mid}, want strictly inside 0..1");
        probe.Want("context-reset", reset, 1f);
        probe.Want("same-frame-duplicate", duplicate, 0f);
        return probe;
    }

    /// <summary>
    /// TreeRow's two overlapping targets. picto's <c>.expandArrow</c>
    /// stops propagation, so a gesture belongs to the arrow or to the row
    /// and never to both; the arrow is reserved AFTER the row and takes
    /// ImGui's active id from it on a press, so release-inside can only
    /// ever be completed by the item that OWNS the press. The two straight
    /// releases are each other's baseline (same row, same frames, one
    /// point apart), and the two drags are the release-inside rule read
    /// from both directions.
    /// </summary>
    private static Probe SidebarRouting(BehaviorHost host)
    {
        // The row sits at (24,24), 120 wide, at depth 0 — so the chevron is
        // the 18px box at its left edge, x 24..42 over the full 26px height,
        // and BOTH points below are inside the row's own rect. Geometry
        // therefore cannot be what separates them; the routing is.
        var arrow = new Vector2(33, 37);
        var label = new Vector2(100, 37);
        var probe = new Probe();
        probe.Want(
            "release-on-arrow", Drive(_ => arrow), "expander=1 selected=0");
        probe.Want(
            "release-on-label", Drive(_ => label), "expander=0 selected=1");
        // Pressed the arrow, released off it: the arrow owns the press and
        // was not hovered at the release, and the row never owned it — so
        // the drag-out cancels outright rather than falling through.
        probe.Want(
            "drag-arrow-to-label",
            Drive(frame => frame < 6 ? arrow : label),
            "expander=0 selected=0");
        // The mirror: the ROW owns the press, the release lands inside the
        // row (the arrow box is part of it), so the row selects — once.
        probe.Want(
            "drag-label-to-arrow",
            Drive(frame => frame < 6 ? label : arrow),
            "expander=0 selected=1");
        // An expander-less row reserves no arrow at all, so the very same
        // point is ordinary row body.
        probe.Want(
            "no-expander-whole-row-selects",
            Drive(_ => arrow, SidebarExpander.None),
            "expander=0 selected=1");
        return probe;

        string Drive(
            Func<int, Vector2> pointer,
            SidebarExpander expander = SidebarExpander.Collapsed)
        {
            int expanded = 0, selected = 0;
            host.Case(Canvas, 16, _ =>
            {
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                var props = new TreeRowProps
                {
                    Icon = TablerIcon.Folder,
                    Expander = expander,
                };
                switch (Ui.TreeRow(
                    "##kernel-sidebar",
                    "Party members",
                    in props,
                    new ControlStyle { Width = UiWidth.Fixed(120) }))
                {
                    case TreeRowAction.Expander: expanded++; break;
                    case TreeRowAction.Selected: selected++; break;
                }
            }, pointer, PressAt(5, 7));
            return $"expander={expanded} selected={selected}";
        }
    }

    /// <summary>
    /// Clicking the clear affordance empties the field AND hands keyboard
    /// focus straight back to it — on the IMMEDIATELY following frame.
    /// <paramref name="gap"/> is how many frames the field is absent after
    /// the clear: zero honors the request, anything else expires it, so
    /// the same identity returning later must NOT be focused.
    /// </summary>
    private static Probe ClearFocus(BehaviorHost host, int gap)
    {
        string text = "hello";
        var target = Offscreen;
        bool focused = false;
        int clearedAt = -1;
        host.Case(new Vector2(240, 80), 16, frame =>
        {
            ImGui.SetCursorScreenPos(new Vector2(10, 20));
            // While the field is away, a request that survived would land
            // on a later control instead.
            if (clearedAt >= 0 && frame - clearedAt <= gap)
            {
                Interactive.Reserve(
                    "##clear-focus-gap", new Vector2(28f), disabled: false);
                return;
            }
            Ui.ClearableTextInput(
                "##kernel-clearable",
                text,
                next => text = next,
                new ControlStyle { Width = UiWidth.Fixed(200) });
            // While the field still holds text the LAST submitted item is
            // the clear hit area, so its own rect is the click target; once
            // cleared it is the input again, whose focus is the subject.
            if (text.Length > 0)
                target = (ImGui.GetItemRectMin()
                    + ImGui.GetItemRectMax()) * 0.5f;
            else if (clearedAt < 0)
                clearedAt = frame;
            else
                focused |= ImGui.IsItemFocused() || ImGui.IsItemActive();
        },
        pointer: frame => frame >= 2 ? target : Offscreen,
        mouse: PressAt(4, 5));

        var probe = new Probe();
        probe.Want("text", text, string.Empty);
        probe.Want("refocused", focused, gap == 0);
        return probe;
    }

    // ---- Shell sidebar: the warm frame's cost --------------------------

    private const int SidebarRows = 300;
    private const int SidebarWarmup = 60;

    /// <summary>The rows the 762px tree shows at the 26px pitch: the clipper's
    /// 30-slot band reaches 780px, which is the section header (24px) plus its
    /// first 30 rows.</summary>
    private const int SidebarVisible = 30;
    private const int SidebarFrames = 600;

    /// <summary>The gate: the p95 warm frame, in milliseconds.</summary>
    private const double SidebarBudgetMs = 1.5;

    private static readonly Vector2 SidebarCanvas = new(320, 840);
    private static readonly Vector2 SidebarBox = new(280, 800);

    private static readonly
        Func<IReadOnlyList<Poser.Domain.Identity.BoneId>, bool> OverlayVisible =
            static _ => true;

    /// <summary>
    /// A 300-row tree at three depths with guides, badges, actor action strips
    /// and overlay toggles, fully expanded and never structurally changed: what
    /// the sidebar costs once its cache is warm. The scene revision is frozen,
    /// so every measured frame is the steady state the shell actually runs in.
    /// </summary>
    internal static int SidebarPerf() =>
        Suite("Poser shell sidebar perf", 320, 840, SidebarPerfCases);

    private static int SidebarPerfCases(BehaviorHost host)
    {
        var vm = SyntheticShell(SidebarRows);
        var sidebar = new Poser.UI.Views.ShellSidebar();
        var baseline = new SidebarBaseline(vm, SidebarVisible);
        var ticks = new long[SidebarFrames];
        var bare = new long[SidebarFrames];
        long cached = Drive(
            host, () => sidebar.Draw(vm, Vector2.Zero, SidebarBox), ticks);
        long painter = Drive(host, baseline.Draw, bare);

        Array.Sort(ticks);
        Array.Sort(bare);
        double p50 = Milliseconds(ticks[SidebarFrames / 2]);
        double p95 = Milliseconds(ticks[(int)(SidebarFrames * 0.95) - 1]);
        double max = Milliseconds(ticks[SidebarFrames - 1]);
        // The same band with no cache at all: what is left after this is what
        // the sidebar's own bookkeeping costs.
        double bareP50 = Milliseconds(bare[SidebarFrames / 2]);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(
            invariant,
            $"sidebar-perf rows={SidebarRows} frames={SidebarFrames} " +
            $"p50={p50:0.####} p95={p95:0.####} max={max:0.####} " +
            $"alloc={cached - painter} painter={painter} total={cached} " +
            $"painter-p50={bareP50:0.####}"));
        host.Check(
            "warm-frame-budget",
            p95 < SidebarBudgetMs,
            string.Create(
                invariant,
                $"p95 {p95:0.####}ms, budget {SidebarBudgetMs:0.##}ms"));
        // What the SIDEBAR owns is the difference between its warm frame and
        // the same band painted with no cache at all: the shared painter's own
        // per-draw bytes (the SVG icon renderer above all) predate this view
        // and are reported, not gated, here.
        host.Check(
            "warm-frame-allocation",
            cached <= painter,
            string.Create(
                invariant,
                $"{cached - painter} bytes over {SidebarFrames} warm frames "
                + $"beyond the painter's {painter} (want <= 0)"));
        // The comparison is worth something only while both cases paint the
        // same band; a drifted visible count shows up as a gap here.
        host.Check(
            "baseline-band",
            cached >= painter * 0.9,
            string.Create(
                invariant,
                $"sidebar {cached} vs painter {painter} - the baseline is no "
                + $"longer painting the sidebar's visible band"));
        return host.Each();
    }

    /// <summary>Warmup, then <see cref="SidebarFrames"/> measured frames of one
    /// draw, bracketed so the harness' own cost stays out of the numbers.
    /// </summary>
    private static long Drive(BehaviorHost host, Action draw, long[]? ticks)
    {
        long allocated = 0;
        host.Case(SidebarCanvas, SidebarWarmup + SidebarFrames, frame =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            draw();
            long elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - started;
            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame < SidebarWarmup)
                return;
            if (ticks != null)
                ticks[frame - SidebarWarmup] = elapsed;
            allocated += after - before;
        }, _ => Offscreen);
        return allocated;
    }

    /// <summary>
    /// The sidebar's visible band, painted straight out of the view model with
    /// no cache, no flattening and no clipper: the shared painter's own cost
    /// for exactly the rows the sidebar shows. The sidebar's bookkeeping may
    /// not add a byte to it.
    /// </summary>
    private sealed class SidebarBaseline
    {
        private readonly Poser.UI.Views.AppShellViewModel _vm;
        private readonly int _rows;
        private readonly Action<Ui.ScrollRegionScope> _tree;
        private readonly Action<string> _search;

        internal SidebarBaseline(
            Poser.UI.Views.AppShellViewModel vm, int rows)
        {
            _vm = vm;
            _rows = rows;
            _tree = Tree;
            _search = next => _vm.SidebarSearch = next;
        }

        internal void Draw()
        {
            var theme = Ui.ActiveTheme;
            float inset = theme.Page.Inset;
            float width = SidebarBox.X;
            ImGui.SetCursorScreenPos(new Vector2(inset, 6f));
            Ui.FilterPill(
                "##baseline-search",
                _vm.SidebarSearch,
                _search,
                "Filter scene...",
                ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(
                        MathF.Max(1f, width - inset * 2f - 1f)),
                });
            ImGui.SetCursorScreenPos(new Vector2(0f, 38f));
            Ui.ScrollRegion("##baseline-tree", width, SidebarBox.Y - 38f, _tree);
        }

        private void Tree(Ui.ScrollRegionScope region)
        {
            var theme = Ui.ActiveTheme;
            float inset = theme.Page.Inset;
            float gutter = theme.Scrollbar.GutterWidth;
            float width = region.ContentWidth + gutter - inset;
            var origin = ImGui.GetCursorScreenPos() + new Vector2(inset, 0f);
            var section = _vm.Sections[0];

            var style = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Weight = FontWeight.Medium,
                Color = theme.TextMuted,
            };
            Ui.TextInBand(
                origin + new Vector2(theme.Spacing.Two, theme.Spacing.Two),
                new Vector2(width, Ui.MeasureText(section.Title, style).Y),
                section.Title,
                style,
                TextAlign.Start);
            float side = theme.Controls.SwitchHeight;
            ImGui.SetCursorScreenPos(
                origin + new Vector2(width - gutter - side, 0f));
            Ui.IconButton(
                TablerIcon.Plus,
                style: ControlStyle.Square(side),
                id: "##baseline-plus");

            float top = theme.Floating.CloseActionSize;
            for (int i = 0; i < _rows; i++)
            {
                var row = section.Rows[i];
                var props = new TreeRowProps
                {
                    Icon = row.Icon,
                    HideIcon = row.Depth > 0,
                    Badge = string.IsNullOrEmpty(row.Count) ? null : row.Count,
                    Depth = row.Depth,
                    Trunks = Trunks(row.TreeLines),
                    IsLastChild = row.IsLastChild,
                    Expander = row.HasChildren
                        ? SidebarExpander.Open
                        : SidebarExpander.None,
                    Selected = row.Active,
                    TrailingInset = gutter,
                    ActionSlots = row.ActorActions
                        ? 3
                        : row.OverlayBones != null ? 1 : 0,
                };
                ImGui.SetCursorScreenPos(origin + new Vector2(
                    0f, top + i * theme.Controls.ListRowHeight));
                Ui.TreeRow(
                    (string)row.Tag!,
                    row.Label,
                    in props,
                    out var actions,
                    new ControlStyle { Width = UiWidth.Fixed(width) });
                if (props.ActionSlots == 0)
                    continue;
                ImGui.PushID((string)row.Tag!);
                float step = side + 2f;
                if (row.ActorActions)
                {
                    ImGui.SetCursorScreenPos(actions);
                    Ui.IconButton(
                        TablerIcon.Crosshair,
                        style: ControlStyle.Square(side),
                        help: "Set game target",
                        id: "##target");
                    ImGui.SetCursorScreenPos(actions + new Vector2(step, 0f));
                    Ui.TemporaryIconToggle(
                        TablerIcon.Eye,
                        selected: false,
                        style: ControlStyle.Square(side),
                        help: row.ActorVisible ? "Hide actor" : "Show actor",
                        id: "##visible",
                        slashed: !row.ActorVisible);
                    ImGui.SetCursorScreenPos(
                        actions + new Vector2(step * 2f, 0f));
                    Ui.TemporaryIconToggle(
                        TablerIcon.PlayerPlay,
                        selected: false,
                        style: ControlStyle.Square(side),
                        help: row.ActorPaused
                            ? "Resume animation"
                            : "Pause animation",
                        id: "##pause",
                        slashed: row.ActorPaused);
                }
                else
                {
                    ImGui.SetCursorScreenPos(actions);
                    Ui.TemporaryIconToggle(
                        TablerIcon.Eye,
                        selected: false,
                        style: ControlStyle.Square(side),
                        help: "Hide from skeleton overlay",
                        id: "##overlay");
                }
                ImGui.PopID();
            }
        }

        private static uint Trunks(bool[]? lines)
        {
            if (lines == null)
                return 0u;
            uint mask = 0u;
            for (int level = 1; level < lines.Length && level < 32; level++)
                if (lines[level])
                    mask |= 1u << level;
            return mask;
        }
    }

    private static double Milliseconds(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The synthetic scene: actors carrying the three-slot action
    /// strip, categories carrying the overlay toggle, and bones at depth 2 with
    /// the guide mask their position implies.</summary>
    private static Poser.UI.Views.AppShellViewModel SyntheticShell(int rows)
    {
        var vm = new Poser.UI.Views.AppShellViewModel
        {
            SceneRevision = 1,
            IsOverlayVisible = OverlayVisible,
        };
        var actors = new Poser.UI.Views.ShellSidebarSection
        {
            Title = "ACTORS",
            ShowPlus = true,
        };
        var scene = new Poser.UI.Views.ShellSidebarSection { Title = "SCENE" };
        vm.Sections.Add(actors);
        vm.Sections.Add(scene);

        const int categories = 3;
        const int bones = 5;
        int made = 0;
        for (int actor = 0; made < rows - 12; actor++)
        {
            actors.Rows.Add(new Poser.UI.Views.ShellSidebarRow
            {
                Label = "Actor " + actor.ToString(),
                Count = "142",
                Icon = TablerIcon.User,
                Depth = 0,
                HasChildren = true,
                Expanded = true,
                Active = actor == 0,
                ActorActions = true,
                ActorVisible = actor % 3 != 0,
                ActorPaused = actor % 4 == 0,
                Tag = "actor:" + actor.ToString(),
            });
            made++;
            for (int category = 0; category < categories && made < rows - 12;
                 category++)
            {
                bool lastCategory = category == categories - 1;
                actors.Rows.Add(new Poser.UI.Views.ShellSidebarRow
                {
                    Label = "Category " + category.ToString(),
                    Depth = 1,
                    HasChildren = true,
                    Expanded = true,
                    IsLastChild = lastCategory,
                    TreeLines = [false, !lastCategory],
                    OverlayBones = [],
                    Tag = string.Concat(
                        "cat:", actor.ToString(), ":", category.ToString()),
                });
                made++;
                for (int bone = 0; bone < bones && made < rows - 12; bone++)
                {
                    actors.Rows.Add(new Poser.UI.Views.ShellSidebarRow
                    {
                        Label = "Bone " + bone.ToString(),
                        Count = bone % 2 == 0 ? "3" : "",
                        Depth = 2,
                        IsLastChild = bone == bones - 1,
                        TreeLines = [false, !lastCategory, true],
                        Tag = string.Concat(
                            "bone:", actor.ToString(), ":",
                            category.ToString(), ":", bone.ToString()),
                    });
                    made++;
                }
            }
        }

        for (int i = made; i < rows; i++)
            scene.Rows.Add(new Poser.UI.Views.ShellSidebarRow
            {
                Label = "Prop " + i.ToString(),
                Icon = TablerIcon.Square,
                Depth = 0,
                Tag = "prop:" + i.ToString(),
            });
        return vm;
    }
}
