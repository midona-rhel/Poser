using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Dalamud.Bindings.ImGui;
using Poser.UI;
using Poser.UI.Reactive;
using Ui = Poser.UI.LegacyCrystarium;
using Rx = Poser.UI.Crystarium;

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
    private readonly List<(string Name, bool Ok, string Detail)> results =
        new();

    internal BehaviorHost(ImFontAtlasPtr atlas) => this.atlas = atlas;

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
        Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null)
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
        using var form = new Form
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
            return cases(new BehaviorHost(ImGui.GetIO().Fonts));
        }
        finally
        {
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

    // ---- Reactive text button: the retained path's own contract -------

    // The reactive fixtures stage at the same (24,24) origin the pixel
    // catalog uses; the button is ~110x32 there, so this point is inside
    // it for BOTH toggle labels as well as "Apply changes".
    private static readonly Vector2 ReactiveCanvas = new(320, 120);
    private static readonly Vector2 ReactiveOrigin = new(24, 24);
    private static readonly Vector2 ReactiveInside =
        ReactiveOrigin + new Vector2(40, 14);
    private static readonly Vector2 ReactiveOutside = new(300, 110);

    private static readonly Action NoOp = static () => { };

    /// <summary>The parity fixture: ONE reactive button, so its warm-frame
    /// bytes compare one-to-one against the identical legacy button. Hoisted
    /// so the callback itself is a retained instance: a delegate allocated
    /// per frame would be measuring the harness, not the runtime.</summary>
    private static readonly Func<UiNode> ParityTree = static () =>
        Rx.Button("Apply changes", NoOp);

    /// <summary>
    /// Tooling-only component (never shipped): one reducer-driven label, so
    /// a click's effect is observable as text. Its state is a record struct,
    /// which is the shape a warm frame must not allocate for.
    /// </summary>
    private sealed class TogglePill
        : StatefulComponent<TogglePill.Props, TogglePill.State>
    {
        /// <summary>What the LAST Render observed — the probe reads the
        /// state the frame actually drew, not the reducer's return.</summary>
        internal static string LastLabel = string.Empty;

        internal readonly record struct Props;

        internal readonly record struct State(bool On);

        /// <summary>The component's OWN mount factory: authors name a
        /// component, never its three type arguments.</summary>
        internal static UiNode Node(UiKey key) =>
            Rx.Component<TogglePill, Props, State>(default, key);

        protected override State CreateState(in Props props) => new(false);

        protected override UiNode Render(in Props props, in State state)
        {
            LastLabel = state.On ? "On" : "Off";
            return Rx.Column(
                Sx.Gap(8f),
                [
                    Rx.Button(
                        LastLabel,
                        UpdateState(static s => s with { On = !s.On })),
                ]);
        }
    }

    internal static int ReactiveButton() =>
        Suite(
            "Crystarium reactive-button behavior", 320, 120,
            ReactiveButtonCases);

    private static int ReactiveButtonCases(BehaviorHost host)
    {
        Func<int, Vector2> on = _ => ReactiveInside;
        Func<int, Vector2> away = _ => Offscreen;
        host.Expect("release-inside", Drive(on, PressAt(5, 7)), 1);
        host.Expect(
            "drag-out",
            Drive(
                frame => frame < 6 ? ReactiveInside : ReactiveOutside,
                PressAt(5, 7)),
            0);
        host.Expect("enter", Drive(away, key: TabThen(ImGuiKey.Enter)), 1);
        // Text-button parity: Space is NOT an activation key, so the
        // retained path must refuse it exactly as the imperative one does.
        host.Expect("space-not", Drive(away, key: TabThen(ImGuiKey.Space)), 0);
        host.Expect(
            "disabled", Drive(on, PressAt(5, 7), disabled: true), 0);
        host.Check("reducer-toggle", ReducerToggle(host));
        host.Check("identity-reorder", IdentityReorder());
        host.Check("identity-collision", IdentityCollision());
        host.Check("identity-pruning", IdentityPruning(host));
        host.Check("cursor-flow", CursorFlow(host));
#if DEBUG
        host.Check("stateful-key-required", StatefulKeyRequired(host));
#endif
        host.Check("allocation-runtime", AllocationRuntime(host));
        host.Check("allocation-parity", AllocationParity(host));
        return host.Summary("reactive-button behavior: all cases pass");

        int Drive(
            Func<int, Vector2> pointer,
            Func<int, (bool HasEvent, bool Down)>? mouse = null,
            Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
            bool disabled = false)
        {
            int activations = 0;
            var root = new UiRoot();
            host.Case(ReactiveCanvas, 12, _ =>
            {
                ImGui.SetCursorScreenPos(ReactiveOrigin);
                root.Render(
                    ReactiveOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => Rx.Button(
                        "Apply changes",
                        () => activations++,
                        disabled: disabled));
            }, pointer, mouse, key);
            return activations;
        }
    }

    /// <summary>
    /// A reducer's result is QUEUED: the activation at the release frame
    /// cannot be observed by the frame that painted the press, only by the
    /// next build. Two full press/release gestures therefore have to read
    /// Off -> On -> Off with each flip landing exactly one frame after its
    /// release.
    /// </summary>
    private static Probe ReducerToggle(BehaviorHost host)
    {
        var root = new UiRoot();
        var labels = new List<string>();
        host.Case(ReactiveCanvas, 22, _ =>
        {
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            root.Render(
                ReactiveOrigin,
                ImGui.GetContentRegionAvail(),
                static () => TogglePill.Node("toggle"));
            labels.Add(TogglePill.LastLabel);
        },
        _ => ReactiveInside,
        frame => frame is 5 or 15
            ? (true, true)
            : frame is 7 or 17 ? (true, false) : default);

        var transitions = new List<string>();
        for (int i = 0; i < labels.Count; i++)
        {
            if (i == 0 || labels[i] != labels[i - 1])
                transitions.Add($"{labels[i]}@{i}");
        }

        var probe = new Probe();
        probe.Want(
            "trace", string.Join(" ", transitions), "Off@0 On@8 Off@18");
        return probe;
    }

    // ---- Identity: the path hash IS the contract ----------------------

    /// <summary>
    /// A KEYED element must keep its identity when it moves among its
    /// siblings — that is the whole point of a key — while an UNKEYED one
    /// must not, because position is all it has. Driven through the REAL
    /// chain function, not a copy of its arithmetic.
    /// </summary>
    private static Probe IdentityReorder()
    {
        const ulong parent = 0x9E3779B97F4A7C15UL;
        const int scope = 3;
        var probe = new Probe();
        probe.Want(
            "keyed-survives-reorder",
            UiRoot.DebugChain(parent, 0, ElementKind.Interactive, 7, scope)
                == UiRoot.DebugChain(parent, 5, ElementKind.Interactive, 7, scope),
            true);
        probe.Want(
            "unkeyed-follows-ordinal",
            UiRoot.DebugChain(parent, 0, ElementKind.Interactive, UiKey.None, scope)
                != UiRoot.DebugChain(parent, 5, ElementKind.Interactive, UiKey.None, scope),
            true);
        return probe;
    }

    /// <summary>
    /// The chain must fold the COMPLETE key, not its 32-bit hash: two long
    /// keys whose <c>GetHashCode</c> folds collide are still two different
    /// rows. <c>ulong.GetHashCode</c> XORs the two dwords, so 0x1 and
    /// 0x0000000200000003 fold identically (1^0 == 3^2) and every UiKey
    /// hash built on that fold collides with them — the precondition the
    /// case asserts before it can mean anything.
    /// </summary>
    private static Probe IdentityCollision()
    {
        const ulong parent = 0x243F6A8885A308D3UL;
        const long first = 0x0000000000000001L;
        const long second = 0x0000000200000003L;
        var probe = new Probe();
        probe.Want(
            "precondition-folds-collide",
            ((UiKey)first).GetHashCode() == ((UiKey)second).GetHashCode(),
            true);
        probe.Want(
            "long-payload-survives-fold",
            UiRoot.DebugChain(parent, 0, ElementKind.Interactive, first, 1)
                != UiRoot.DebugChain(parent, 0, ElementKind.Interactive, second, 1),
            true);
        probe.Want(
            "literal-ab-vs-ba",
            UiRoot.DebugChain(parent, 0, ElementKind.Interactive, "ab", 1)
                != UiRoot.DebugChain(parent, 0, ElementKind.Interactive, "ba", 1),
            true);
        return probe;
    }

    private static readonly Func<UiNode> TwoButtonTree = static () =>
        Rx.Column(Sx.Gap(4f), [Rx.Button("One", NoOp), Rx.Button("Two", NoOp)]);

    private static readonly Func<UiNode> OneButtonTree = static () =>
        Rx.Column(Sx.Gap(4f), [Rx.Button("One", NoOp)]);

    /// <summary>The id cache is keyed by PATH, so a tree that stops drawing
    /// a row must stop paying for it: an unvisited entry is dropped at the
    /// end of the frame that skipped it.</summary>
    private static Probe IdentityPruning(BehaviorHost host)
    {
        var root = new UiRoot();
        int wide = -1;
        int narrow = -1;
        host.Case(ReactiveCanvas, 20, frame =>
        {
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            root.Render(
                ReactiveOrigin,
                ImGui.GetContentRegionAvail(),
                frame < 10 ? TwoButtonTree : OneButtonTree);
            if (frame == 9)
                wide = root.DebugInteractionIdCount;
            if (frame == 19)
                narrow = root.DebugInteractionIdCount;
        }, _ => Offscreen);

        var probe = new Probe();
        probe.Want("two-buttons", wide, 2);
        probe.Want("one-button", narrow, 1);
        return probe;
    }

    /// <summary>
    /// The cursor contract: a root paints absolutely but reserves its
    /// arranged extent ONCE, so the item rect around the call is the whole
    /// tree and imperative content written afterwards flows below it.
    /// </summary>
    private static Probe CursorFlow(BehaviorHost host)
    {
        var root = new UiRoot();
        Vector2 rootOrigin = default;
        Vector2 extent = default;
        Vector2 itemMin = default;
        Vector2 itemMax = default;
        float belowTop = 0f;
        host.Case(new Vector2(320, 240), 3, _ =>
        {
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            Ui.Button("Above", id: "##cursor-above");
            rootOrigin = ImGui.GetCursorScreenPos();
            extent = Ui.MeasureButton("Apply changes");
            root.Render(
                rootOrigin, ImGui.GetContentRegionAvail(), ParityTree);
            itemMin = ImGui.GetItemRectMin();
            itemMax = ImGui.GetItemRectMax();
            Ui.Button("Below", id: "##cursor-below");
            belowTop = ImGui.GetItemRectMin().Y;
        }, _ => Offscreen);

        var probe = new Probe();
        // Every other want below compares defaults against defaults if the
        // case never drew, so the extent is asserted non-empty first.
        probe.Want(
            "root-has-extent", extent.X > 0f && extent.Y > 0f, true);
        probe.Want("item-min", itemMin, rootOrigin);
        probe.Want("item-max", itemMax, rootOrigin + extent);
        probe.Want(
            "below-flows-under",
            belowTop >= rootOrigin.Y + extent.Y,
            true);
        return probe;
    }

#if DEBUG
    /// <summary>An unkeyed stateful mount is a bug the runtime refuses to
    /// carry: matched by position, its state would follow its neighbour
    /// through any reorder.</summary>
    private static Probe StatefulKeyRequired(BehaviorHost host)
    {
        var root = new UiRoot();
        bool threw = false;
        host.Case(ReactiveCanvas, 1, _ =>
        {
            try
            {
                ImGui.SetCursorScreenPos(ReactiveOrigin);
                root.Render(
                    ReactiveOrigin,
                    ImGui.GetContentRegionAvail(),
                    static () => TogglePill.Node(UiKey.None));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
        }, _ => Offscreen);

        var probe = new Probe();
        probe.Want("unkeyed-mount-throws", threw, true);
        return probe;
    }
#endif

    /// <summary>
    /// A warm frame must allocate NOTHING: every buffer is pooled, every
    /// reducer and handler is a retained delegate, and the interaction id
    /// strings are formatted once. The legacy button is measured under the
    /// identical host only when the reactive number is non-zero, so a
    /// failure carries its own baseline instead of a bare byte count.
    /// </summary>
    // The PBI gate is "construction ADDS no allocation on a warm frame":
    // (a) the retained runtime alone allocates zero, and (b) a reactive
    // button costs no more than the identical legacy button. The shared
    // painter's own per-frame bytes (MeasureText/Reserve marshalling)
    // predate this runtime and are reported by (b)'s numbers, not owned
    // by this suite.
    private static Probe AllocationRuntime(BehaviorHost host)
    {
        long runtime = MeasureTree(host, LeaflessTree);
        var probe = new Probe();
        if (runtime != 0)
            probe.Fault(
                $"runtime construction allocated {runtime} bytes over "
                + "100 warm frames (want 0)");
        return probe;
    }

    private static Probe AllocationParity(BehaviorHost host)
    {
        long reactive = MeasureAllocation(host, reactive: true);
        long legacy = MeasureAllocation(host, reactive: false);
        var probe = new Probe();
        if (reactive > legacy)
            probe.Fault(
                $"one reactive button allocated {reactive} bytes over 100 "
                + $"warm frames; the identical legacy button {legacy} — the "
                + "retained path added bytes of its own");
        return probe;
    }

    /// <summary>Everything the RUNTIME owns and nothing the legacy painter
    /// does: build, children, styles, a keyed component scope, reducer-token
    /// construction, layout, paint walk, and scope commit. This is the tree
    /// the zero-byte gate measures.</summary>
    private static readonly Func<UiNode> LeaflessTree = static () =>
        Rx.Column(
            Sx.Gap(8f),
            [
                TokenProbe.Node("probe"),
                Rx.Column(),
                Rx.Column(),
            ]);

    /// <summary>Tooling-only component with no legacy leaf: its Render
    /// constructs an UpdateState token every frame, so the reducer-cache
    /// path sits inside the zero-byte measurement.</summary>
    private sealed class TokenProbe
        : StatefulComponent<TokenProbe.Props, TokenProbe.State>
    {
        internal readonly record struct Props;

        internal readonly record struct State(bool On);

        internal static UiNode Node(UiKey key) =>
            Rx.Component<TokenProbe, Props, State>(default, key);

        protected override State CreateState(in Props props) => new(false);

        protected override UiNode Render(in Props props, in State state)
        {
            _ = UpdateState(static s => s with { On = !s.On });
            return Rx.Column();
        }
    }

    /// <summary>Bytes allocated by the DRAW BODY over frames 20..119.
    /// Bracketing the body rather than the whole case keeps the harness's
    /// own per-frame cost out of the number.</summary>
    private static long MeasureTree(BehaviorHost host, Func<UiNode> tree)
    {
        var root = new UiRoot();
        long allocated = 0;
        host.Case(ReactiveCanvas, 120, frame =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            root.Render(
                ReactiveOrigin, ImGui.GetContentRegionAvail(), tree);
            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame >= 20)
                allocated += after - before;
        }, _ => Offscreen);
        return allocated;
    }

    private static long MeasureAllocation(BehaviorHost host, bool reactive)
    {
        var root = new UiRoot();
        long allocated = 0;
        host.Case(ReactiveCanvas, 120, frame =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            if (reactive)
            {
                root.Render(
                    ReactiveOrigin,
                    ImGui.GetContentRegionAvail(),
                    ParityTree);
            }
            else
            {
                Ui.Button("Apply changes", id: "##alloc-legacy");
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame >= 20)
                allocated += after - before;
        }, _ => Offscreen);
        return allocated;
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
    /// SidebarRow's two overlapping targets. picto's <c>.expandArrow</c>
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
        // The row sits at (24,24), 120 wide, at --row-inset 21 — so
        // .expandArrow is the 16px gutter box at x 24..40 over the full
        // 26px height, and BOTH points below are inside the row's own
        // rect. Geometry therefore cannot be what separates them; the
        // routing is.
        var arrow = new Vector2(32, 37);
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
                var props = new SidebarRowProps
                {
                    Icon = TablerIcon.Folder,
                    Inset = 21f,
                    Expander = expander,
                };
                switch (Ui.SidebarRow(
                    "##kernel-sidebar",
                    "Party members",
                    in props,
                    new ControlStyle { Width = UiWidth.Fixed(120) }))
                {
                    case SidebarRowAction.Expander: expanded++; break;
                    case SidebarRowAction.Selected: selected++; break;
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
}
