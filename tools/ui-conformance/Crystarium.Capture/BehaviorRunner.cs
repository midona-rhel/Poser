using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI;
using Poser.UI.Reactive;
using Ui = Poser.UI.LegacyCrystarium;
using Rx = Poser.UI.Crystarium;
// WinForms owns the hidden host window, so its Button/Label collide with the
// retained vocabulary's prop-bags by name; the aliases pick the UI ones.
using Button = Poser.UI.Button;
using Label = Poser.UI.Label;

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
        new Button { Label = "Apply changes", OnClick = NoOp };

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
            return new Column
            {
                Style = new() { Layout = new() { Gap = 8f } },
                Children =
                [
                    new Button
                    {
                        Label = LastLabel,
                        OnClick = UpdateState(static s => s with { On = !s.On }),
                    },
                ],
            };
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
        host.Check("nullable-state", NullableState(host));
        host.Check("interactive-composition", InteractiveComposition(host));
        host.Check("identity-reorder", IdentityReorder());
        host.Check("identity-collision", IdentityCollision());
        host.Check("identity-pruning", IdentityPruning(host));
        host.Check("cursor-flow", CursorFlow(host));
        host.Check("stateful-key-required", StatefulKeyRequired(host));
#if DEBUG
        host.Check("stale-children", StaleChildren(host));
        host.Check("stale-event", StaleEvent(host));
        host.Check("foreign-root-children", ForeignRootChildren(host));
#endif
        host.Check("allocation-runtime", AllocationRuntime(host));
        host.Check("allocation-parity", AllocationParity(host));
        host.Check("allocation-dynamic-props", AllocationDynamicProps(host));
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
                    () => new Button
                    {
                        Label = "Apply changes",
                        OnClick = (Action)(() => activations++),
                        Disabled = disabled,
                    });
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

    /// <summary>
    /// Tooling-only component whose state is a NULLABLE reference: null is a
    /// legitimate value here, not "unset". Mount, promotion and reducer
    /// chaining must therefore all key off their own flags — a runtime that
    /// read "PendingState is null" as "nothing queued" would swallow the null
    /// frame and leave the previous value on screen.
    /// </summary>
    private sealed class NullableCell
        : StatefulComponent<NullableCell.Props, string?>
    {
        /// <summary>What the LAST Render observed, with the null frame
        /// spelled out so the trace can name it.</summary>
        internal static string LastSeen = string.Empty;

        internal readonly record struct Props;

        internal static UiNode Node(UiKey key) =>
            Rx.Component<NullableCell, Props, string?>(default, key);

        protected override string? CreateState(in Props props) => "a";

        protected override UiNode Render(in Props props, in string? state)
        {
            LastSeen = state ?? "<null>";
            // The caption is CONSTANT so the hit box never moves; the state
            // under test is read from the probe, not from the label.
            return new Column
            {
                Style = new() { Layout = new() { Gap = 8f } },
                Children =
                [
                    new Button
                    {
                        Label = "Cycle",
                        OnClick = UpdateState(static s => s is null ? "b" : null),
                    },
                ],
            };
        }
    }

    /// <summary>Three gestures cycle "a" -> null -> "b" -> null, each flip
    /// landing one frame after its release exactly as a non-null reducer's
    /// does.</summary>
    private static Probe NullableState(BehaviorHost host)
    {
        var root = new UiRoot();
        var seen = new List<string>();
        host.Case(ReactiveCanvas, 32, _ =>
        {
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            root.Render(
                ReactiveOrigin,
                ImGui.GetContentRegionAvail(),
                static () => NullableCell.Node("nullable"));
            seen.Add(NullableCell.LastSeen);
        },
        _ => ReactiveInside,
        frame => frame is 5 or 15 or 25
            ? (true, true)
            : frame is 7 or 17 or 27 ? (true, false) : default);

        var transitions = new List<string>();
        for (int i = 0; i < seen.Count; i++)
        {
            if (i == 0 || seen[i] != seen[i - 1])
                transitions.Add($"{seen[i]}@{i}");
        }

        var probe = new Probe();
        probe.Want(
            "trace",
            string.Join(" ", transitions),
            "a@0 <null>@8 b@18 <null>@28");
        return probe;
    }

    // ---- Composition: a hit box around ordinary content ----------------

    // Inside any plausible measurement of the "hit me" run, and far enough
    // from the edges that rounding cannot put it outside.
    private static readonly Vector2 ComposedInside =
        ReactiveOrigin + new Vector2(6, 6);

    /// <summary>
    /// A clickable built from PUBLIC vocabulary alone — no painter, no
    /// declared box, no internals. Its hit area can only have come from the
    /// composed text, so release-inside activating and drag-out not is proof
    /// the element measured its own content.
    /// </summary>
    private static Probe InteractiveComposition(BehaviorHost host)
    {
        var probe = new Probe();
        probe.Want("release-inside", Drive(_ => ComposedInside), 1);
        probe.Want(
            "drag-out",
            Drive(frame => frame < 6 ? ComposedInside : ReactiveOutside),
            0);
        return probe;

        int Drive(Func<int, Vector2> pointer)
        {
            int hits = 0;
            var root = new UiRoot();
            host.Case(ReactiveCanvas, 12, _ =>
            {
                ImGui.SetCursorScreenPos(ReactiveOrigin);
                root.Render(
                    ReactiveOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => new Element
                    {
                        Children = [new Label { Text = "hit me" }],
                        On = new Listeners
                        {
                            OnClick = (Action)(() => hits++),
                        },
                    });
            }, pointer, PressAt(5, 7));
            return hits;
        }
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
        // The kind byte is gone from the chain: there is now ONE element, so
        // ordinal, key and scope are the whole of an identity.
        probe.Want(
            "keyed-survives-reorder",
            UiRoot.DebugChain(parent, 0, 7, scope)
                == UiRoot.DebugChain(parent, 5, 7, scope),
            true);
        probe.Want(
            "unkeyed-follows-ordinal",
            UiRoot.DebugChain(parent, 0, UiKey.None, scope)
                != UiRoot.DebugChain(parent, 5, UiKey.None, scope),
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
        // The kind byte is gone from the chain: there is now ONE element.
        probe.Want(
            "long-payload-survives-fold",
            UiRoot.DebugChain(parent, 0, first, 1)
                != UiRoot.DebugChain(parent, 0, second, 1),
            true);
        probe.Want(
            "literal-ab-vs-ba",
            UiRoot.DebugChain(parent, 0, "ab", 1)
                != UiRoot.DebugChain(parent, 0, "ba", 1),
            true);
        return probe;
    }

    private static readonly Func<UiNode> TwoButtonTree = static () =>
        new Column
        {
            Style = new() { Layout = new() { Gap = 4f } },
            Children =
            [
                new Button { Label = "One", OnClick = NoOp },
                new Button { Label = "Two", OnClick = NoOp },
            ],
        };

    private static readonly Func<UiNode> OneButtonTree = static () =>
        new Column
        {
            Style = new() { Layout = new() { Gap = 4f } },
            Children = [new Button { Label = "One", OnClick = NoOp }],
        };

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

    /// <summary>An unkeyed stateful mount is a bug the runtime refuses to
    /// carry in ANY configuration: matched by position, its state would
    /// follow its neighbour through any reorder, so the refusal is an
    /// ArgumentException that ships rather than a DEBUG assertion.</summary>
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
            catch (ArgumentException)
            {
                threw = true;
            }
        }, _ => Offscreen);

        var probe = new Probe();
        probe.Want("unkeyed-mount-throws", threw, true);
        return probe;
    }

#if DEBUG
    // ---- Arena-handle provenance --------------------------------------
    //
    // A UiChildren range and a UiEvent token are both INDICES into per-frame
    // arena storage, exactly as a UiNode is. Carried into the next frame or
    // into another root they would address a stranger, so each is stamped
    // with its arena and frame and checked where it is consumed. The static
    // fields below are the smuggling channel the cases need.

    private static UiChildren staleChildren;
    private static UiEvent staleEvent;
    private static UiChildren foreignChildren;

    private static readonly Func<UiNode> CaptureChildren = static () =>
    {
        staleChildren = [new Label { Text = "captured" }];
        return new Column { Children = staleChildren };
    };

    private static readonly Func<UiNode> ReuseChildren = static () =>
        new Column { Children = staleChildren };

    private static readonly Func<UiNode> CaptureEvent = static () =>
        EventProbe.Node("stale-event");

    private static readonly Func<UiNode> ReuseEvent = static () =>
        new Button { Label = "Stale", OnClick = staleEvent };

    private static readonly Func<UiNode> CaptureForeign = static () =>
    {
        foreignChildren = [new Label { Text = "owned" }];
        return new Column { Children = foreignChildren };
    };

    private static readonly Func<UiNode> ReuseForeign = static () =>
        new Column { Children = foreignChildren };

    /// <summary>Tooling-only component that leaks its own reducer token so a
    /// LATER frame can try to bind it.</summary>
    private sealed class EventProbe
        : StatefulComponent<EventProbe.Props, EventProbe.State>
    {
        internal readonly record struct Props;

        internal readonly record struct State(bool On);

        internal static UiNode Node(UiKey key) =>
            Rx.Component<EventProbe, Props, State>(default, key);

        protected override State CreateState(in Props props) => new(false);

        protected override UiNode Render(in Props props, in State state)
        {
            staleEvent = UpdateState(static s => s with { On = !s.On });
            return new Column();
        }
    }

    private static Probe StaleChildren(BehaviorHost host) =>
        Provenance(
            host, "stale-children-message",
            "stale children from a previous frame", CaptureChildren,
            ReuseChildren);

    private static Probe StaleEvent(BehaviorHost host) =>
        Provenance(
            host, "stale-event-message",
            "stale event from a previous frame", CaptureEvent, ReuseEvent);

    /// <summary>Both roots render on the SAME frame index, so the frame
    /// stamps match and only the arena identity can be what rejects the
    /// range.</summary>
    private static Probe ForeignRootChildren(BehaviorHost host)
    {
        var owner = new UiRoot();
        var stranger = new UiRoot();
        string message = "no throw";
        host.Case(ReactiveCanvas, 1, _ =>
        {
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            owner.Render(
                ReactiveOrigin, ImGui.GetContentRegionAvail(),
                CaptureForeign);
            try
            {
                ImGui.SetCursorScreenPos(ReactiveOrigin);
                stranger.Render(
                    ReactiveOrigin, ImGui.GetContentRegionAvail(),
                    ReuseForeign);
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }
        }, _ => Offscreen);

        var probe = new Probe();
        probe.Want("message", message, "children from another root");
        return probe;
    }

    /// <summary>Frame 0 captures the handle, frame 1 consumes it under the
    /// same root: the arena matches, so only the frame stamp can reject
    /// it.</summary>
    private static Probe Provenance(
        BehaviorHost host,
        string name,
        string expected,
        Func<UiNode> capture,
        Func<UiNode> reuse)
    {
        var root = new UiRoot();
        string message = "no throw";
        host.Case(ReactiveCanvas, 2, frame =>
        {
            try
            {
                ImGui.SetCursorScreenPos(ReactiveOrigin);
                root.Render(
                    ReactiveOrigin,
                    ImGui.GetContentRegionAvail(),
                    frame == 0 ? capture : reuse);
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }
        }, _ => Offscreen);

        var probe = new Probe();
        probe.Want(name, message, expected);
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
        new Column
        {
            Style = new() { Layout = new() { Gap = 8f } },
            Children =
            [
                TokenProbe.Node("probe"),
                new Column(),
                new Column(),
            ],
        };

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
            return new Column();
        }
    }

    /// <summary>Props that CHANGE every frame. A build closing over this
    /// would allocate a delegate per frame; travelling as an argument to a
    /// static <see cref="UiBuilder{TProps}"/> it must not.</summary>
    private readonly record struct GapProps(float Gap);

    /// <summary>Boxes only: the known legacy leaf cost (MeasureText and
    /// Reserve marshalling) is not this case's subject, so no leaf is
    /// drawn.</summary>
    private static readonly UiBuilder<GapProps> GapTree =
        static (in GapProps props) => new Column
        {
            Style = new() { Layout = new() { Gap = props.Gap } },
            Children = [new Column(), new Column()],
        };

    private static Probe AllocationDynamicProps(BehaviorHost host)
    {
        var root = new UiRoot();
        long allocated = 0;
        host.Case(ReactiveCanvas, 120, frame =>
        {
            // Three distinct gaps, so the tree really is rebuilt from
            // changing inputs rather than a constant hoisted by the JIT.
            var props = new GapProps(4f + (frame % 3) * 2f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(ReactiveOrigin);
            root.Render(
                ReactiveOrigin, ImGui.GetContentRegionAvail(), in props,
                GapTree);
            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame >= 20)
                allocated += after - before;
        }, _ => Offscreen);

        var probe = new Probe();
        if (allocated != 0)
            probe.Fault(
                $"a tree built from CHANGING props allocated {allocated} "
                + "bytes over 100 warm frames (want 0)");
        return probe;
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

    // ---- Reactive dropdown: the portal control's own contract ----------
    //
    // Everything here is out of the pixel fixtures' reach: a menu that opens
    // on a real click, a row that reports its index, the four ways a menu
    // closes, and the first typed UiEvent<TValue> dispatch. Nothing is a
    // hardcoded rectangle — the trigger and row boxes are derived from the
    // SAME measurement seams the control lays itself out with, so a fixture
    // that drifts fails as a geometry probe rather than as a mystery.

    private static readonly Vector2 DropCanvas = new(360, 320);
    private static readonly Vector2 DropOrigin = new(24, 24);
    private static readonly Vector2 DropOutside = new(340, 300);

    private static readonly string[] DropItems =
    [
        "Date Added",
        "Date Created",
        "Date Modified",
        "Name",
        "Rating",
        "File Size",
        "Duration",
    ];

    private static readonly Action<int> DropNoOp = static _ => { };

    /// <summary>
    /// Past the 32-element threshold the arena's scratch span replaced: a
    /// menu this long used to cost a <c>new UiNode[n]</c> every frame, so the
    /// warm-frame comparison against the identical legacy control is what
    /// proves the scratch path allocates nothing.
    /// </summary>
    private static readonly string[] DropItemsLarge = BuildLargeItems();

    private static string[] BuildLargeItems()
    {
        var items = new string[40];
        for (int i = 0; i < items.Length; i++)
            items[i] = "Option " + i.ToString(
                "00", System.Globalization.CultureInfo.InvariantCulture);
        return items;
    }

    private static readonly Func<UiNode> DropLargeTree = static () =>
        new Dropdown { Items = DropItemsLarge, Selected = 0, OnChange = DropNoOp };

    /// <summary>The parity fixture: one closed reactive dropdown, so its
    /// warm-frame bytes compare one-to-one against the identical legacy
    /// control. Hoisted for the same reason <see cref="ParityTree"/> is.
    /// </summary>
    private static readonly Func<UiNode> DropParityTree = static () =>
        new Dropdown { Items = DropItems, Selected = 0, OnChange = DropNoOp };

    /// <summary>The trigger and menu boxes the fixtures aim at, all read off
    /// the control's own seams: <c>MeasureDropdown</c> for the trigger,
    /// <c>MeasureDropdownPopup</c> for the panel, and
    /// <c>FloatingSurface</c>'s anchored placement (below the anchor at the
    /// shared gap, with CmSelect's remainder riding on the anchor) for where
    /// the panel lands.</summary>
    private readonly record struct DropGeometry(
        Vector2 TriggerMin,
        Vector2 TriggerMax,
        Vector2 PopupMin,
        Vector2 PopupMax,
        float RowHeight,
        float RowGap,
        float DropInset)
    {
        internal Vector2 TriggerCenter => (TriggerMin + TriggerMax) * 0.5f;

        internal Vector2 RowCenter(int index) => new(
            (PopupMin.X + PopupMax.X) * 0.5f,
            PopupMin.Y + DropInset
                + index * (RowHeight + RowGap) + RowHeight * 0.5f);
    }

    private static DropGeometry dropGeometry;

    private static DropGeometry MeasureDrop()
    {
        float scale = ImGuiHelpers.GlobalScale;
        Ui.DropdownMetrics metrics =
            Ui.MeasureDropdown(DropItems, null, default);
        Ui.DropdownPopupMetrics popup =
            Ui.MeasureDropdownPopup(DropItems.Length, metrics.LogicalHeight);
        var triggerMax =
            DropOrigin + new Vector2(metrics.Width, metrics.Height);
        var popupMin = new Vector2(
            DropOrigin.X,
            triggerMax.Y + popup.AnchorGapCompensation
                + Ui.ActiveTheme.Floating.AnchorGap * scale);
        return new DropGeometry(
            DropOrigin,
            triggerMax,
            popupMin,
            popupMin + new Vector2(metrics.Width, popup.PopupHeight),
            popup.RowHeight,
            popup.RowGap,
            popup.DropInset);
    }

    /// <summary>The same control one row position to the right, derived by
    /// TRANSLATION rather than by a second measurement — the seams are only
    /// callable inside a case.</summary>
    private static DropGeometry Beside(in DropGeometry geometry, float dx)
    {
        var delta = new Vector2(dx, 0f);
        return geometry with
        {
            TriggerMin = geometry.TriggerMin + delta,
            TriggerMax = geometry.TriggerMax + delta,
            PopupMin = geometry.PopupMin + delta,
            PopupMax = geometry.PopupMax + delta,
        };
    }

    internal static int ReactiveDropdown() =>
        Suite(
            "Crystarium reactive-dropdown behavior", 360, 320,
            ReactiveDropdownCases);

    private static int ReactiveDropdownCases(BehaviorHost host)
    {
        host.Check("geometry", DropGeometryProbe(host));
        DropGeometry geo = dropGeometry;
        Vector2 trigger = geo.TriggerCenter;
        Vector2 row0 = geo.RowCenter(0);
        Vector2 row2 = geo.RowCenter(2);

        // Reserve reports Clicked on the PRESS frame, so every gesture below
        // is named by its press; the release two frames later only ends it.
        host.Expect(
            "open-select",
            Drive(
                16,
                frame => frame < 6 ? trigger : row2,
                Presses(2, 8)),
            "fired=1 last=2");
        host.Expect(
            "select-closes",
            Drive(
                22,
                frame => frame < 6 ? trigger : row2,
                Presses(2, 8, 14)),
            "fired=1 last=2");
        // Row 0 IS the selected row: it reports nothing and still closes, so
        // the follow-up aimed at a DIFFERENT row proves the close happened
        // rather than merely that the reselect was silent.
        host.Expect(
            "reselect-noop",
            Drive(
                22,
                frame => frame < 6 ? trigger : frame < 12 ? row0 : row2,
                Presses(2, 8, 14)),
            "fired=0 last=-1");
        host.Expect(
            "outside-dismiss",
            Drive(
                22,
                frame => frame < 6
                    ? trigger
                    : frame < 12 ? DropOutside : row2,
                Presses(2, 8, 14)),
            "fired=0 last=-1");
        host.Expect(
            "escape-dismiss",
            Drive(
                22,
                frame => frame < 6 ? trigger : row2,
                Presses(2, 14),
                frame => frame switch
                {
                    8 => (true, ImGuiKey.Escape, true),
                    9 => (true, ImGuiKey.Escape, false),
                    _ => default,
                }),
            "fired=0 last=-1");
        host.Expect(
            "disabled-no-open",
            Drive(
                16,
                frame => frame < 6 ? trigger : row2,
                Presses(2, 8),
                disabled: true),
            "fired=0 last=-1");
        // Text-button parity in reverse: the imperative dropdown opens on
        // POINTER click only, so the retained trigger must refuse Enter.
        host.Expect(
            "keyboard-parity",
            Drive(
                18,
                frame => frame < 8 ? Offscreen : row2,
                Presses(12),
                TabThen(ImGuiKey.Enter)),
            "fired=0 last=-1");
        host.Check("uievent-int", UiEventInt(host));
        host.Check("supersession", Supersession(host));
        host.Check(
            "allocation-closed-parity", DropAllocationParity(host, false));
        host.Check(
            "allocation-open-parity", DropAllocationParity(host, true));
        host.Check("allocation-large", DropAllocationLarge(host));
        return host.Summary("reactive-dropdown behavior: all cases pass");

        string Drive(
            int frames,
            Func<int, Vector2> pointer,
            Func<int, (bool HasEvent, bool Down)> mouse,
            Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
            bool disabled = false)
        {
            int fired = 0;
            int last = -1;
            var root = new UiRoot();
            host.Case(DropCanvas, frames, _ =>
            {
                ImGui.SetCursorScreenPos(DropOrigin);
                root.Render(
                    DropOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => new Dropdown
                    {
                        Items = DropItems,
                        Selected = 0,
                        OnChange = (Action<int>)(index =>
                        {
                            fired++;
                            last = index;
                        }),
                        Disabled = disabled,
                    });
            }, pointer, mouse, key);
            return $"fired={fired} last={last}";
        }
    }

    /// <summary>A press/release pair at each listed DOWN frame; the release
    /// lands two frames later, so a gesture never straddles the next.
    /// </summary>
    private static Func<int, (bool HasEvent, bool Down)> Presses(
        params int[] downs) =>
        frame =>
        {
            foreach (int down in downs)
            {
                if (frame == down)
                    return (true, true);
                if (frame == down + 2)
                    return (true, false);
            }

            return default;
        };

    /// <summary>
    /// The derived geometry, checked against what the runtime actually
    /// reserved before any case aims at it: the root reserves its arranged
    /// extent, and a portal is out of flow, so the item rect around Render
    /// IS the trigger. Without this a missed click would read as a broken
    /// contract instead of a stale rectangle.
    /// </summary>
    private static Probe DropGeometryProbe(BehaviorHost host)
    {
        var root = new UiRoot();
        DropGeometry computed = default;
        Vector2 itemMin = default;
        Vector2 itemMax = default;
        host.Case(DropCanvas, 2, _ =>
        {
            computed = MeasureDrop();
            ImGui.SetCursorScreenPos(DropOrigin);
            root.Render(
                DropOrigin, ImGui.GetContentRegionAvail(), DropParityTree);
            itemMin = ImGui.GetItemRectMin();
            itemMax = ImGui.GetItemRectMax();
        }, _ => Offscreen);
        dropGeometry = computed;

        var probe = new Probe();
        probe.Want("trigger-origin", itemMin, computed.TriggerMin);
        // The walk rounds its boxes and the measurement seam does not, so a
        // sub-pixel span difference is expected; a ROW of difference is not.
        probe.Want(
            "trigger-span",
            Vector2.Distance(itemMax, computed.TriggerMax) <= 1f,
            true);
        probe.Want(
            "popup-on-canvas", computed.PopupMax.Y <= DropCanvas.Y, true);
        probe.Want(
            "rows-inside-popup",
            computed.RowCenter(DropItems.Length - 1).Y
                < computed.PopupMax.Y - computed.DropInset,
            true);
        return probe;
    }

    /// <summary>
    /// Tooling-only component (never shipped) bound through the TYPED event
    /// path: the chosen index rides the dispatch record rather than a
    /// captured closure, so this is the first proof a
    /// <c>UiEvent&lt;TValue&gt;</c> reaches its reducer with its value.
    /// </summary>
    private sealed class SelectCell
        : StatefulComponent<SelectCell.Props, SelectCell.State>
    {
        /// <summary>What the LAST Render observed.</summary>
        internal static int LastSeen = -1;

        internal readonly record struct Props;

        internal readonly record struct State(int Selected);

        internal static UiNode Node(UiKey key) =>
            Rx.Component<SelectCell, Props, State>(default, key);

        protected override State CreateState(in Props props) => new(0);

        protected override UiNode Render(in Props props, in State state)
        {
            LastSeen = state.Selected;
            return new Dropdown
            {
                Items = DropItems,
                Selected = state.Selected,
                OnChange = UpdateState<int>(static (s, i) => s with { Selected = i }),
            };
        }
    }

    private static Probe UiEventInt(BehaviorHost host)
    {
        DropGeometry geo = dropGeometry;
        Vector2 trigger = geo.TriggerCenter;
        Vector2 row3 = geo.RowCenter(3);
        var root = new UiRoot();
        var seen = new List<int>();
        SelectCell.LastSeen = -1;
        host.Case(DropCanvas, 16, _ =>
        {
            ImGui.SetCursorScreenPos(DropOrigin);
            root.Render(
                DropOrigin,
                ImGui.GetContentRegionAvail(),
                static () => SelectCell.Node("select"));
            seen.Add(SelectCell.LastSeen);
        },
        frame => frame < 6 ? trigger : row3,
        Presses(2, 8));

        var transitions = new List<string>();
        for (int i = 0; i < seen.Count; i++)
        {
            if (i == 0 || seen[i] != seen[i - 1])
                transitions.Add($"{seen[i]}@{i}");
        }

        var probe = new Probe();
        // The reducer is QUEUED: the row's press at frame 8 can only be
        // observed by the build that follows it.
        probe.Want("trace", string.Join(" ", transitions), "0@0 3@9");
        return probe;
    }

    // ---- Supersession: one open menu at a time -------------------------
    //
    // SIDE BY SIDE, and that is the whole design of the fixture. A menu is an
    // exclusive surface that hangs DOWNWARD from its trigger, so two stacked
    // dropdowns put B underneath A's panel: the press meant to supersede A
    // would land on one of A's own rows instead, and if it landed on the
    // SELECTED row it would take the silent reselect-close path — a=0,
    // aLast=-1, menu closed, B never reached. That reproduces a supersession
    // failure exactly without any supersession bug existing, so the case
    // would be measuring its own layout. Placing B horizontally clear makes
    // the collision impossible by construction rather than by arithmetic,
    // and `b-clear-of-a-menu` asserts it so the fixture cannot drift back.
    private static readonly Vector2 SupersessionCanvas = new(680, 320);
    private const float SupersessionGap = 40f;

    /// <summary>
    /// Two independent reactive dropdowns, one root. The ACCEPTED backend's
    /// dismissal policy (ImGui popups make every other window's content
    /// unhoverable while open) means the first press on B's trigger only
    /// dismisses A — it does not open B; the second press opens B normally.
    /// This case asserts PARITY with that accepted behavior, anchored by the
    /// imperative twin run at identical geometry: if either path ever gains
    /// or loses first-press supersession, the anchor breaks and the case
    /// fails. Making the first press open B would be a deliberate behavior
    /// change to the shared backend — a product decision, not a twin's.
    /// </summary>
    private static Probe Supersession(BehaviorHost host)
    {
        DropGeometry a = dropGeometry;
        DropGeometry b = Beside(
            in a, a.TriggerMax.X - a.TriggerMin.X + SupersessionGap);

        var probe = new Probe();
        // The fixture's own precondition, asserted before anything is read
        // from it: B's trigger must be clear of A's panel, or every result
        // below is a statement about occlusion instead of supersession.
        probe.Want("b-clear-of-a-menu", b.TriggerMin.X >= a.PopupMax.X, true);
        // The BASELINE the other two are read against: B on its own, opened
        // and selected with no A in the story at all. Without it a silent
        // "b=0" could equally mean a broken supersession or a stale
        // rectangle, and the case would name the wrong culprit.
        probe.Want(
            "b-alone", Alone(b.RowCenter(1)), "a=0 aLast=-1 b=1 bLast=1");
        probe.Want(
            "a-rows-gone", Drive(a.RowCenter(2)),
            "a=0 aLast=-1 b=0 bLast=-1");
        // First press on B's uncovered trigger: dismisses A ONLY. The value
        // asserted here is the accepted backend's, proven by the twin below.
        string reactive = Drive(b.RowCenter(1));
        probe.Want(
            "first-press-dismisses-only", reactive,
            "a=0 aLast=-1 b=0 bLast=-1");
        // The parity anchor: the imperative control at identical geometry
        // under the identical script. Either path changing first-press
        // semantics breaks this equality before anything else does.
        probe.Want("legacy-parity", reactive, Legacy(b.RowCenter(1)));
        probe.Want(
            "second-press-opens", Retry(b.RowCenter(1)),
            "a=0 aLast=-1 b=1 bLast=1");

        // Evidence, printed unconditionally so a failure arrives with the
        // frames that produced it instead of a bare counter.
        Console.WriteLine(
            $"supersession-geometry a-trigger={Rect(a.TriggerMin, a.TriggerMax)} "
            + $"a-popup={Rect(a.PopupMin, a.PopupMax)} "
            + $"b-trigger={Rect(b.TriggerMin, b.TriggerMax)}");
        Console.WriteLine("supersession-trace " + Trace(b.RowCenter(1)));
        return probe;

        static string Rect(Vector2 min, Vector2 max) =>
            $"x{min.X:0}..{max.X:0},y{min.Y:0}..{max.Y:0}";

        // The IMPERATIVE control at the identical geometry under the
        // identical script: the one comparison that separates "the retained
        // path regressed" from "neither path supersedes".
        string Legacy(Vector2 row)
        {
            int aFired = 0, bFired = 0, aLast = -1, bLast = -1;
            host.Case(SupersessionCanvas, 20, _ =>
            {
                var content = new ControlStyle { Width = UiWidth.Content };
                ImGui.SetCursorScreenPos(a.TriggerMin);
                Ui.Dropdown(
                    "##sup-legacy-a", DropItems, 0,
                    index => { aFired++; aLast = index; }, content);
                ImGui.SetCursorScreenPos(b.TriggerMin);
                Ui.Dropdown(
                    "##sup-legacy-b", DropItems, 0,
                    index => { bFired++; bLast = index; }, content);
            },
            frame => frame < 6
                ? a.TriggerCenter
                : frame < 12 ? b.TriggerCenter : row,
            Presses(2, 8, 14));
            return $"a={aFired} aLast={aLast} b={bFired} bLast={bLast}";
        }

        // A open, then TWO presses on B's trigger, then B's row: the first
        // press is spent dismissing A (the accepted policy), the second
        // opens B, and the row then fires exactly once.
        string Retry(Vector2 row) => Run(
            26,
            frame => frame < 6
                ? a.TriggerCenter
                : frame < 18 ? b.TriggerCenter : row,
            Presses(2, 8, 14, 20));

        // Per-frame pointer occlusion at world scope, which is what decides
        // whether B's trigger can see the press at all.
        string Trace(Vector2 row)
        {
            var frames = new List<string>();
            int aFired = 0, bFired = 0;
            var root = new UiRoot();
            host.Case(SupersessionCanvas, 20, frame =>
            {
                ImGui.SetCursorScreenPos(DropOrigin);
                root.Render(
                    DropOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => new Row
                    {
                        Style = new()
                        {
                            Layout = new() { Gap = SupersessionGap },
                        },
                        Children =
                        [
                            new Dropdown
                            {
                                Items = DropItems,
                                Selected = 0,
                                OnChange = (Action<int>)(_ => aFired++),
                                Key = "a",
                            },
                            new Dropdown
                            {
                                Items = DropItems,
                                Selected = 0,
                                OnChange = (Action<int>)(_ => bFired++),
                                Key = "b",
                            },
                        ],
                    });
                frames.Add(
                    $"{frame}:{(Interactive.PointerOccluded() ? "occ" : "free")}"
                    + $"/a{aFired}b{bFired}");
            },
            frame => frame < 6
                ? a.TriggerCenter
                : frame < 12 ? b.TriggerCenter : row,
            Presses(2, 8, 14));
            return string.Join(" ", frames);
        }

        string Alone(Vector2 row) => Run(
            16,
            frame => frame < 6 ? b.TriggerCenter : row,
            Presses(2, 8));

        string Drive(Vector2 third) => Run(
            20,
            frame => frame < 6
                ? a.TriggerCenter
                : frame < 12 ? b.TriggerCenter : third,
            Presses(2, 8, 14));

        string Run(
            int frames,
            Func<int, Vector2> pointer,
            Func<int, (bool HasEvent, bool Down)> mouse)
        {
            int aFired = 0, bFired = 0, aLast = -1, bLast = -1;
            var root = new UiRoot();
            host.Case(SupersessionCanvas, frames, _ =>
            {
                ImGui.SetCursorScreenPos(DropOrigin);
                root.Render(
                    DropOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => new Row
                    {
                        Style = new()
                        {
                            Layout = new() { Gap = SupersessionGap },
                        },
                        Children =
                        [
                            new Dropdown
                            {
                                Items = DropItems,
                                Selected = 0,
                                OnChange = (Action<int>)(
                                    index => { aFired++; aLast = index; }),
                                Key = "a",
                            },
                            new Dropdown
                            {
                                Items = DropItems,
                                Selected = 0,
                                OnChange = (Action<int>)(
                                    index => { bFired++; bLast = index; }),
                                Key = "b",
                            },
                        ],
                    });
            }, pointer, mouse);
            return $"a={aFired} aLast={aLast} b={bFired} bLast={bLast}";
        }
    }

    /// <summary>The dropdown's own warm-frame gate: the retained control may
    /// cost no more than the identical imperative one, closed or open. Both
    /// sides run under the SAME host and the SAME input script, so the open
    /// comparison is a menu each path opened by a real click.</summary>
    private static Probe DropAllocationParity(BehaviorHost host, bool open)
    {
        long reactive = MeasureDropAllocation(host, true, open);
        long legacy = MeasureDropAllocation(host, false, open);
        var probe = new Probe();
        if (reactive > legacy)
            probe.Fault(
                $"a {(open ? "open" : "closed")} reactive dropdown allocated "
                + $"{reactive} bytes over 100 warm frames; the identical "
                + $"legacy dropdown {legacy} — the retained path added bytes "
                + "of its own");
        else
            Console.WriteLine(
                $"allocation-{(open ? "open" : "closed")}-parity "
                + $"reactive={reactive} legacy={legacy}");
        return probe;
    }

    /// <summary>
    /// The >32-item gate. A 40-row menu is past the threshold the arena's
    /// scratch span replaced, so the comparison against the identical legacy
    /// 40-item control is what shows the per-frame row buffer costs nothing.
    /// Closed, because the subject is CONSTRUCTION: the rows are built every
    /// frame whether the menu is up or not.
    /// </summary>
    private static Probe DropAllocationLarge(BehaviorHost host)
    {
        long reactive = MeasureDropAllocation(
            host, true, false, DropItemsLarge, DropLargeTree,
            "##alloc-legacy-large");
        long legacy = MeasureDropAllocation(
            host, false, false, DropItemsLarge, DropLargeTree,
            "##alloc-legacy-large");
        var probe = new Probe();
        if (reactive > legacy)
            probe.Fault(
                $"a closed {DropItemsLarge.Length}-item reactive dropdown "
                + $"allocated {reactive} bytes over 100 warm frames; the "
                + $"identical legacy dropdown {legacy} — the retained path "
                + "added bytes of its own");
        else
            Console.WriteLine(
                $"allocation-large items={DropItemsLarge.Length} "
                + $"reactive={reactive} legacy={legacy}");
        return probe;
    }

    private static long MeasureDropAllocation(
        BehaviorHost host, bool reactive, bool open) =>
        MeasureDropAllocation(
            host, reactive, open, DropItems, DropParityTree,
            "##alloc-legacy-dropdown");

    private static long MeasureDropAllocation(
        BehaviorHost host, bool reactive, bool open, string[] items,
        Func<UiNode> tree, string legacyId)
    {
        Vector2 trigger = dropGeometry.TriggerCenter;
        var root = new UiRoot();
        long allocated = 0;
        host.Case(DropCanvas, 120, frame =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(DropOrigin);
            if (reactive)
            {
                root.Render(
                    DropOrigin,
                    ImGui.GetContentRegionAvail(),
                    tree);
            }
            else
            {
                Ui.Dropdown(legacyId, items, 0, DropNoOp);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame >= 20)
                allocated += after - before;
        },
        frame => open && frame is >= 1 and <= 4 ? trigger : Offscreen,
        open ? Presses(2) : null);
        return allocated;
    }

    // ---- Reactive picker (PBI-015 wave O) -----------------------------
    //
    // The picker is a REDESIGN, so pixels are judged against Picto and the
    // contract is judged here: that the surface opens on its trigger, that
    // the filter island's typing reaches the component's own state, that a
    // single-select row picks its ITEM and closes, that a multi-select row
    // toggles and does NOT, and that the multi variant is genuinely
    // controlled — it reports flips and stores nothing.

    private static readonly Vector2 PickCanvas = new(400, 440);
    private static readonly Vector2 PickOrigin = new(24, 24);
    private static readonly Vector2 PickOutside = new(380, 420);

    private static readonly string[] PickItems =
    [
        "Date Added",
        "Date Created",
        "Date Modified",
        "Name",
        "Rating",
        "File Size",
        "Duration",
    ];

    /// <summary>The panel and row boxes, all read off the control's own
    /// numbers: the trigger from the button seam, the panel from the picker's
    /// token arithmetic, and the placement from FloatingSurface's anchored
    /// rule (below the anchor at the shared gap).</summary>
    private readonly record struct PickGeometry(
        Vector2 TriggerMin, Vector2 TriggerMax, Vector2 PanelMin, Vector2 PanelMax)
    {
        internal Vector2 TriggerCenter => (TriggerMin + TriggerMax) * 0.5f;

        /// <summary>Centre of the .header band: inside the surface, so pointer
        /// occlusion reports it, but on no row and no field — the one point
        /// that observes an open picker without touching it.</summary>
        internal Vector2 HeaderCenter => new(
            PanelMin.X + 120f, PanelMin.Y + Rx.PickerHeaderHeight * 0.5f);

        /// <summary>Centre of the search field, for the click that focuses it.
        /// </summary>
        internal Vector2 SearchCenter => new(
            PanelMin.X + 120f,
            PanelMin.Y + Rx.PickerHeaderHeight + Rx.PickerSearchHeight * 0.5f);

        internal Vector2 RowCenter(int index) => new(
            PanelMin.X + 120f,
            PanelMin.Y + Rx.PickerHeaderHeight + Rx.PickerSearchHeight
                + index * Rx.PickerRowHeight + Rx.PickerRowHeight * 0.5f);
    }

    private static PickGeometry pickGeometry;

    private static PickGeometry MeasurePick()
    {
        float scale = ImGuiHelpers.GlobalScale;
        var triggerMax = PickOrigin + new Vector2(
            Ui.IntrinsicButtonWidth("Date Modified", default),
            Ui.ButtonHeight(default));
        var panelMin = new Vector2(
            PickOrigin.X,
            triggerMax.Y + Ui.ActiveTheme.Floating.AnchorGap * scale);
        // The same clamp/height arithmetic the component runs, so a fixture
        // that drifts fails as a geometry probe rather than as a mystery.
        int rows = Math.Clamp(
            PickItems.Length,
            Ui.ActiveTheme.Picker.MinimumRows,
            Ui.ActiveTheme.Picker.MaximumRows);
        float panelHeight = Ui.ActiveTheme.Floating.PopoverPadding * 2f
            + Ui.ActiveTheme.Controls.ListRowHeight
            + Ui.ActiveTheme.Spacing.Two
            + Ui.ActiveTheme.Controls.WorkspaceHeight
            + Ui.ActiveTheme.Spacing.Two
            + rows * Ui.ActiveTheme.Controls.ListRowHeight;
        return new PickGeometry(
            PickOrigin,
            triggerMax,
            panelMin,
            panelMin + new Vector2(Ui.ActiveTheme.Picker.Width, panelHeight) * scale);
    }

    /// <summary>What a picker run observed. One shape for both variants: the
    /// single one never toggles and the multi one never picks, so a stray
    /// dispatch on the wrong callback shows up as a non-empty trace.</summary>
    private sealed class PickTally
    {
        public int Picks;
        public string PickTrace = string.Empty;
        public string ToggleTrace = string.Empty;
        public int Opens;
        public int OpenFrames;
        /// <summary>Whether a surface still owned the exclusive chain on the
        /// LAST frame — which is what "the popup is still up" means.</summary>
        public bool Open;

        public void Pick(string item)
        {
            Picks++;
            PickTrace = PickTrace.Length == 0 ? item : PickTrace + "|" + item;
        }

        public void Toggle(string item, bool selected)
        {
            string entry = item + (selected ? "+" : "-");
            ToggleTrace = ToggleTrace.Length == 0
                ? entry
                : ToggleTrace + "|" + entry;
        }

        public void Opened() => Opens++;

        public override string ToString() =>
            $"opens={Opens} picks={Picks} pick=[{PickTrace}] "
            + $"toggles=[{ToggleTrace}] open={Open}";
    }

    internal static int ReactivePicker() =>
        Suite("Crystarium reactive-picker behavior", 400, 440, ReactivePickerCases);

    private static int ReactivePickerCases(BehaviorHost host)
    {
        host.Check("geometry", PickGeometryProbe(host));
        PickGeometry geo = pickGeometry;
        Vector2 trigger = geo.TriggerCenter;

        // (1) The trigger opens the surface AND tells the caller to load it.
        host.Expect(
            "open-on-trigger",
            Single(host, 14, f => f < 6 ? trigger : geo.HeaderCenter, Presses(2))
                .ToString(),
            "opens=1 picks=0 pick=[] toggles=[] open=True");

        // (2) A row picks its ITEM, exactly once, and the surface closes. The
        //     second press lands where row 2 was: nothing may answer it.
        PickTally select = Single(
            host, 24,
            f => f < 6 ? trigger : geo.RowCenter(2),
            Presses(2, 8, 16));
        host.Expect(
            "select-dispatches",
            $"{select.Picks} {select.PickTrace}", "1 Date Modified");
        host.Expect(
            "select-closes", $"{select.OpenFrames > 0} {select.Open}", "True False");

        // (3) Reselecting the row that is ALREADY chosen still reports it: a
        //     single-select picker has no silent row, unlike a menu.
        host.Expect(
            "reselect-reports",
            Single(host, 16, f => f < 6 ? trigger : geo.RowCenter(1), Presses(2, 8))
                .PickTrace,
            "Date Created");

        // (4) A press outside dismisses without picking anything.
        host.Expect(
            "dismiss-outside",
            Single(
                host, 24,
                f => f < 6 ? trigger : f < 14 ? PickOutside : geo.RowCenter(2),
                Presses(2, 8, 16)).ToString(),
            "opens=1 picks=0 pick=[] toggles=[] open=False");

        host.Check("filter-types", FilterTyping(host));

        // ---- multi variant ------------------------------------------------
        // (5) Toggling reports the flip and LEAVES THE SURFACE OPEN.
        PickTally toggle = Multi(
            host, 24, f => f < 6 ? trigger : geo.RowCenter(2), Presses(2, 8, 16));
        host.Expect(
            "toggle-does-not-close", toggle.Open && toggle.OpenFrames > 0, true);
        // Row 2 is not in the fixture's selected set, so both presses report it
        // turning ON: the flip is derived from what the CALLER shows, and this
        // caller (deliberately) shows nothing changing.
        host.Expect(
            "toggle-controlled", toggle.ToggleTrace,
            "Date Modified+|Date Modified+");

        // (6) Two DIFFERENT rows accumulate, in order, with no close between.
        PickTally accumulate = Multi(
            host, 30,
            f => f < 6 ? trigger : f < 14 ? geo.RowCenter(0) : geo.RowCenter(3),
            Presses(2, 8, 16));
        host.Expect(
            "multiple-toggles-accumulate", accumulate.ToggleTrace,
            "Date Added+|Name+");
        host.Expect("accumulate-stays-open", accumulate.Open, true);

        // (7) Dismissal is not a revert: the callbacks fired before it and the
        //     selection lives with the caller, so closing changes nothing the
        //     component owned — it owned none of it.
        PickTally dismissed = Multi(
            host, 30,
            f => f < 6 ? trigger : f < 14 ? geo.RowCenter(0) : PickOutside,
            Presses(2, 8, 16));
        host.Expect(
            "dismiss-keeps-selection",
            $"{dismissed.ToggleTrace} open={dismissed.Open}",
            "Date Added+ open=False");

        host.Check("allocation-closed-parity", PickAllocationParity(host));
        return host.Summary("reactive-picker behavior: all cases pass");
    }

    /// <summary>The derived geometry, checked against what the runtime actually
    /// reserved: the root reserves its arranged extent and a portal is out of
    /// flow, so the item rect around Render IS the trigger.</summary>
    private static Probe PickGeometryProbe(BehaviorHost host)
    {
        var root = new UiRoot();
        PickGeometry computed = default;
        Vector2 itemMin = default;
        Vector2 itemMax = default;
        host.Case(PickCanvas, 2, _ =>
        {
            computed = MeasurePick();
            ImGui.SetCursorScreenPos(PickOrigin);
            root.Render(
                PickOrigin, ImGui.GetContentRegionAvail(),
                () => Rx.PickerSurface(PickProps(false, null, null), "probe"));
            itemMin = ImGui.GetItemRectMin();
            itemMax = ImGui.GetItemRectMax();
        }, _ => Offscreen);
        pickGeometry = computed;

        var probe = new Probe();
        probe.Want("trigger-origin", itemMin, computed.TriggerMin);
        probe.Want(
            "trigger-span",
            Vector2.Distance(itemMax, computed.TriggerMax) <= 1f, true);
        // The panel must fit BELOW the trigger on this canvas, or every row
        // point below is a statement about the flip rule instead of the rows.
        probe.Want("panel-on-canvas", computed.PanelMax.Y <= PickCanvas.Y, true);
        probe.Want(
            "rows-inside-body",
            computed.RowCenter(5).Y < computed.PanelMax.Y, true);
        return probe;
    }

    /// <summary>
    /// The filter island reaching the component's own state. The field is
    /// focused the way a user focuses it — a click — then three characters are
    /// typed; the assertion is POSITIONAL, because a filtered list is only
    /// observable as which item the first row now stands for.
    /// </summary>
    private static Probe FilterTyping(BehaviorHost host)
    {
        PickGeometry geo = pickGeometry;
        var probe = new Probe();
        // Unfiltered, row 0 is "Date Added".
        probe.Want("row0-unfiltered", Row0(null), "Date Added");
        // "rat" matches "Rating" alone, so row 0 becomes it — which can only
        // be true if the typed characters reached the component's query.
        probe.Want("row0-filtered", Row0("rat"), "Rating");
        return probe;

        string Row0(string? typed)
        {
            var tally = Single(
                host, 40,
                frame => frame < 6
                    ? geo.TriggerCenter
                    : frame < 14 ? geo.SearchCenter : geo.RowCenter(0),
                Presses(2, 8, 30),
                text: typed is null
                    ? null
                    : frame => frame == 16 ? typed : null);
            return tally.PickTrace;
        }
    }

    /// <summary>The picker's warm-frame gate. The retained control builds its
    /// whole surface every frame, open or closed, so the comparison is against
    /// the imperative pair the legacy Appearance row draws for the same closed
    /// state: the trigger button plus the picker's own idle Draw.</summary>
    private static readonly Action PickNoOp = static () => { };

    private static readonly Action<string> PickNoOpPick = static _ => { };

    /// <summary>Hoisted for the same reason the dropdown's parity tree is: a
    /// build closure allocated per frame would be the harness's cost charged to
    /// the runtime.</summary>
    private static readonly Func<UiNode> PickRowTree = static () =>
        Rx.FormSelectorPicker(
            "Model", "Date Modified", "Sort by", PickItems,
            static item => item, static item => item, "Date Created", null,
            PickNoOpPick, PickNoOp, PickNoOp, available: true, owned: true);

    /// <summary>The same row past the scratch threshold: forty items build
    /// forty rows every frame whether the surface is up or not, so this is what
    /// shows a CLOSED picker's rows cost nothing.</summary>
    private static readonly string[] PickItemsLarge = BuildPickItems();

    private static string[] BuildPickItems()
    {
        var items = new string[40];
        for (int i = 0; i < items.Length; i++)
            items[i] = "Option " + i.ToString(
                "00", System.Globalization.CultureInfo.InvariantCulture);
        return items;
    }

    private static readonly Func<UiNode> PickLargeRowTree = static () =>
        Rx.FormSelectorPicker(
            "Model", "Date Modified", "Sort by", PickItemsLarge,
            static item => item, static item => item, "Option 01", null,
            PickNoOpPick, PickNoOp, PickNoOp, available: true, owned: true);

    /// <summary>
    /// The picker's warm-frame gate, CALIBRATED against the retained control
    /// that already shipped rather than against the imperative row.
    ///
    /// <para>Two measured facts decide the shape of this case. First, the
    /// retained runtime costs bytes PER DECLARED ROW on every warm frame,
    /// whether the surface is up or not — the shipped dropdown does it too, and
    /// this case measures that rather than assuming it. Second, the retained
    /// path cannot early-return the way an idle imperative picker does: the
    /// popup call IS the open test, so a closed retained surface pays for its
    /// placement every frame. Neither is the picker's doing, and a straight
    /// <c>reactive &lt;= legacy</c> assertion would therefore be a claim about
    /// the runtime wearing this control's name.</para>
    ///
    /// <para>So the ASSERTION is the one thing that is this control's own: the
    /// picker's richer row — a check slot, a box and a truncating label — may
    /// cost no more per row than the menu's plain one. The imperative row's
    /// number is reported beside it, ungated, because it is the number a reader
    /// will want and not the number this control can be held to.</para>
    /// </summary>
    private static Probe PickAllocationParity(BehaviorHost host)
    {
        const int span = 40 - 7;
        long reactive = MeasurePickAllocation(host, PickRowTree);
        long large = MeasurePickAllocation(host, PickLargeRowTree);
        long legacy = MeasurePickAllocation(host, null);
        long pickPerRow = (large - reactive) / span;
        // The shipped retained control's own per-row cost, measured here under
        // the same host and the same 100 warm frames, so the comparison cannot
        // drift against a number written down once.
        long menu = MeasureDropAllocation(host, true, false);
        long menuLarge = MeasureDropAllocation(
            host, true, false, DropItemsLarge, DropLargeTree,
            "##alloc-legacy-large");
        long menuPerRow = (menuLarge - menu) / span;

        var probe = new Probe();
        if (pickPerRow > menuPerRow)
            probe.Fault(
                $"a closed picker row costs {pickPerRow} bytes a warm frame "
                + $"against the shipped retained menu's {menuPerRow} — the "
                + "picker's row added bytes of its own");
        Console.WriteLine(
            $"allocation-closed picker={reactive} picker-large={large} "
            + $"picker-per-row={pickPerRow} menu-per-row={menuPerRow} "
            + $"legacy-row={legacy}");
        return probe;
    }

    private static long MeasurePickAllocation(BehaviorHost host, Func<UiNode>? tree)
    {
        var root = new UiRoot();
        var legacyPicker = new Ui.SearchPicker<string>("alloc");
        long allocated = 0;
        host.Case(PickCanvas, 120, frame =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(PickOrigin);
            if (tree is not null)
            {
                root.Render(PickOrigin, ImGui.GetContentRegionAvail(), tree);
            }
            else
            {
                // PageForm.Selector's control cell, drawn with the same seams
                // it uses: the measured label, the Fill-width trigger carrying
                // the truncated value, the permanent Reset slot, and the idle
                // picker the row owns.
                var workspace = ControlStyle.Workspace;
                Ui.Text("Model");
                float resetWidth = Ui.IntrinsicButtonWidth("Reset", workspace);
                string display = Ui.TruncateText(
                    "Date Modified",
                    new TextStyle { Size = Ui.ActiveTheme.Typography.LabelSize },
                    200f);
                ImGui.SetCursorScreenPos(PickOrigin);
                Ui.Button(
                    display, PickNoOp,
                    style: workspace with { Width = UiWidth.Fixed(200f) },
                    id: "##alloc-picker-trigger");
                ImGui.SetCursorScreenPos(PickOrigin + new Vector2(220f, 0f));
                Ui.Button(
                    "Reset", PickNoOp,
                    style: workspace with { Width = UiWidth.Fixed(resetWidth) },
                    help: "Restore the incoming model exactly",
                    id: "##alloc-picker-reset");
                legacyPicker.Draw();
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            if (frame >= 20)
                allocated += after - before;
        }, _ => Offscreen);
        return allocated;
    }

    private static PickerProps<string> PickProps(
        bool multi, PickTally? tally, IReadOnlySet<string>? selected) =>
        new(
            "Date Modified",
            "Sort by",
            PickItems,
            static item => item,
            static item => item,
            multi ? null : "Date Created",
            multi ? selected ?? PickEmpty : null,
            null,
            multi || tally is null ? null : tally.Pick,
            multi && tally is not null ? tally.Toggle : null,
            // OnOpen is a UiHandler now, so the absent case is `default`
            // rather than a null delegate.
            (Action?)(tally is null ? null : tally.Opened),
            Dense: false,
            Disabled: false,
            DisabledHelp: null,
            Multi: multi,
            TriggerWidth: default);

    private static readonly IReadOnlySet<string> PickEmpty =
        new HashSet<string>();

    private static PickTally Single(
        BehaviorHost host, int frames, Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)> mouse,
        Func<int, string?>? text = null) =>
        Run(host, frames, pointer, mouse, text, multi: false);

    private static PickTally Multi(
        BehaviorHost host, int frames, Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)> mouse) =>
        Run(host, frames, pointer, mouse, null, multi: true);

    private static PickTally Run(
        BehaviorHost host, int frames, Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)> mouse, Func<int, string?>? text,
        bool multi)
    {
        var tally = new PickTally();
        var root = new UiRoot();
        host.Case(PickCanvas, frames, _ =>
        {
            ImGui.SetCursorScreenPos(PickOrigin);
            root.Render(
                PickOrigin, ImGui.GetContentRegionAvail(),
                () => Rx.PickerSurface(PickProps(multi, tally, null), "case"));
            // Openness is read from the KERNEL, not inferred from a callback:
            // a surface that closed itself never tells anyone. An exclusive
            // surface occludes the world while it is up, which is the same
            // signal the supersession case reads.
            tally.Open = Interactive.PointerOccluded();
            if (tally.Open)
                tally.OpenFrames++;
        }, pointer, mouse, null, null, text);
        return tally;
    }

    // ---- Reactive form system (PBI-015 wave P) ------------------------
    //
    // The twins are byte-gated against their imperative counterparts, so the
    // PIXELS need nothing from this file. What the sheet cannot reach is
    // everything the form system does between frames: a drag that has to
    // report a value per frame and stop the moment the button comes up, a
    // controlled toggle that must fire exactly once, a popup a retained
    // element opens over a path-derived id, a disclosure whose content
    // appears next frame while its chevron keeps animating, the readout's
    // one-frame controlled lag, and a reset slot that is permanent but empty
    // until the row is owned. Every rectangle below is derived from the
    // control's own tokens and checked against what the runtime reserved.

    private static readonly Vector2 FormCanvas = new(400, 240);
    private static readonly Vector2 FormOrigin = new(24, 24);
    private static readonly Vector2 FormOutside = new(380, 220);

    /// <summary>The bare-control fixtures' measure. 300 logical px is wider
    /// than the label column plus the value column, so a form row's slider
    /// track and its reset slot both have real spans to aim at.</summary>
    private static readonly Vector2 FormRowSize = new(300, 40);

    private static readonly Action<float> FormNoOpFloat = static _ => { };

    private static readonly Action<bool> FormNoOpBool = static _ => { };

    private static readonly Action<Vector4> FormNoOpColor = static _ => { };

    /// <summary>The fixed 200px slider the catalog fixture draws, so the
    /// geometry probe measures the same box the sheet gates.</summary>
    private static readonly Func<UiNode> FormSliderTree = static () =>
        new Slider
        {
            Value = 0.4f,
            Min = 0f,
            Max = 1f,
            OnChange = FormNoOpFloat,
            StyleSheet = new() { Layout = new() { Width = UiDim.Fixed(200f) } },
        };

    private static readonly Func<UiNode> FormSwitchTree = static () =>
        new Switch { Value = false, OnToggle = FormNoOpBool };

    /// <summary>
    /// The boxes the form fixtures aim at, all read off the controls' own
    /// tokens: the slider is the catalog's fixed 200px track, the switch its
    /// intrinsic pill, and the well its square side. Each is checked against
    /// what the runtime actually reserved before any case aims at it, so a
    /// missed press reads as a stale rectangle rather than a broken contract.
    /// </summary>
    private readonly record struct FormGeometry(
        Vector2 SliderMin,
        Vector2 SliderMax,
        Vector2 SwitchMin,
        Vector2 SwitchMax,
        Vector2 WellMin,
        Vector2 WellMax)
    {
        internal Vector2 SwitchCenter => (SwitchMin + SwitchMax) * 0.5f;

        internal Vector2 WellCenter => (WellMin + WellMax) * 0.5f;

        /// <summary>
        /// The pointer x <paramref name="offset"/> WHOLE pixels along the span
        /// the thumb's centre can occupy — the same inset-by-half-a-thumb span
        /// <c>SliderValueAt</c> inverts. Whole pixels because ImGui truncates a
        /// queued mouse position to integers: a fractional target would be
        /// delivered as a different point than the one the expectation was
        /// computed from, and the case would be measuring the harness.
        /// </summary>
        internal float TrackX(int offset) =>
            SliderMin.X + (SliderMax.Y - SliderMin.Y) * 0.5f + offset;

        internal Vector2 TrackPoint(int offset) => new(
            TrackX(offset), (SliderMin.Y + SliderMax.Y) * 0.5f);

        /// <summary>The value the runtime will report for a pointer at
        /// <paramref name="offset"/>, through the SAME seam the walk uses.
        /// </summary>
        internal float ValueAt(int offset) => Ui.SliderValueAt(
            TrackX(offset), SliderMin, SliderMax, 0f, 1f);
    }

    private static FormGeometry formGeometry;

    internal static int ReactiveForm() =>
        Suite("Crystarium reactive-form behavior", 400, 240, ReactiveFormCases);

    private static int ReactiveFormCases(BehaviorHost host)
    {
        host.Check("geometry", FormGeometryProbe(host));
        host.Check("slider-drag", SliderDrag(host));
        host.Check("slider-disabled", SliderDisabled(host));
        host.Check("switch-toggle", SwitchToggle(host));
        host.Check("colorwell-popup", ColorWellPopup(host));
        host.Check("section-toggle", SectionToggle(host));
        host.Check("form-slider-readout", FormSliderReadout(host));
        host.Check("form-selector-reset", FormSelectorReset(host));
        host.Check("allocation-form-row", FormRowAllocation(host));
        return host.Summary("reactive-form behavior: all cases pass");
    }

    /// <summary>The derived boxes against what the runtime reserved. The root
    /// reserves its arranged extent, and each of these trees is ONE leaf, so
    /// the item rect around Render IS that leaf's box.</summary>
    private static Probe FormGeometryProbe(BehaviorHost host)
    {
        var probe = new Probe();
        (Vector2 Min, Vector2 Max) slider = Reserved(FormSliderTree);
        (Vector2 Min, Vector2 Max) toggle = Reserved(FormSwitchTree);
        (Vector2 Min, Vector2 Max) well = Reserved(FormColorWellTree);
        formGeometry = new FormGeometry(
            slider.Min, slider.Max, toggle.Min, toggle.Max, well.Min, well.Max);

        var expected = default((Vector2 Slider, Vector2 Switch, float Well));
        host.Case(FormCanvas, 1, _ =>
        {
            Theme.ControlTokens controls = Ui.ActiveTheme.Controls;
            float scale = ImGuiHelpers.GlobalScale;
            expected = (
                new Vector2(200f, controls.SliderHeight) * scale,
                new Vector2(controls.SwitchWidth, controls.SwitchHeight) * scale,
                controls.ColorWellSize * scale);
        }, _ => Offscreen);

        probe.Want("slider-origin", slider.Min, FormOrigin);
        probe.Want("slider-span", slider.Max - slider.Min, expected.Slider);
        probe.Want("switch-origin", toggle.Min, FormOrigin);
        probe.Want("switch-span", toggle.Max - toggle.Min, expected.Switch);
        probe.Want("well-origin", well.Min, FormOrigin);
        probe.Want(
            "well-span", well.Max - well.Min,
            new Vector2(expected.Well, expected.Well));
        return probe;

        (Vector2 Min, Vector2 Max) Reserved(Func<UiNode> tree)
        {
            var root = new UiRoot();
            Vector2 min = default;
            Vector2 max = default;
            host.Case(FormCanvas, 2, _ =>
            {
                ImGui.SetCursorScreenPos(FormOrigin);
                root.Render(FormOrigin, ImGui.GetContentRegionAvail(), tree);
                min = ImGui.GetItemRectMin();
                max = ImGui.GetItemRectMax();
            }, _ => Offscreen);
            return (min, max);
        }
    }

    /// <summary>Where the pointer sits on each frame of the drag script: it
    /// hovers the track before the press, walks right across ten frames, then
    /// holds its final position well before the release. Holding is what makes
    /// the last reported value unambiguous — the runtime reports on CHANGE, so
    /// a still pointer reports nothing and the release cannot add one more.
    /// </summary>
    private const int DragDownFrame = 2;

    private const int DragUpFrame = 18;

    /// <summary>The pointer's final resting offset along the thumb span, in
    /// whole pixels. 140 of the fixed track's 186 is comfortably inside it and
    /// well clear of every earlier step.</summary>
    private const int DragFinalOffset = 140;

    private static int DragOffset(int frame) => frame switch
    {
        < 4 => 18,
        <= 13 => 18 + (frame - 3) * 9,
        < 20 => DragFinalOffset,
        // AFTER the release the pointer keeps moving, still over the track.
        // "Reports only while active" is only proved by a move the control
        // has to ignore; a pointer left still would prove nothing.
        _ => DragFinalOffset + 8,
    };

    /// <summary>
    /// The drag contract: a held track reports a value per pointer move, the
    /// values walk with the pointer, the last one is the value under the
    /// pointer where the drag ended, and nothing is reported outside the hold.
    /// The fixture feeds each reported value straight back, which is what a
    /// controlled caller does and what makes the sequence a real trace rather
    /// than a repeated delta against a frozen 0.4.
    /// </summary>
    private static Probe SliderDrag(BehaviorHost host)
    {
        FormGeometry geo = formGeometry;
        var frames = new List<int>();
        var values = new List<float>();
        float value = 0.4f;
        var root = new UiRoot();
        int current = 0;
        host.Case(FormCanvas, 26, frame =>
        {
            current = frame;
            ImGui.SetCursorScreenPos(FormOrigin);
            root.Render(
                FormOrigin,
                ImGui.GetContentRegionAvail(),
                () => new Slider
                {
                    Value = value,
                    Min = 0f,
                    Max = 1f,
                    OnChange = (Action<float>)(next =>
                    {
                        value = next;
                        frames.Add(current);
                        values.Add(next);
                    }),
                    StyleSheet = new()
                    {
                        Layout = new() { Width = UiDim.Fixed(200f) },
                    },
                });
        },
        frame => geo.TrackPoint(DragOffset(frame)),
        PressAt(DragDownFrame, DragUpFrame));

        var probe = new Probe();
        probe.Want("reported", values.Count >= 3, true);
        if (values.Count == 0)
            return probe;
        bool monotonic = true;
        for (int i = 1; i < values.Count; i++)
            monotonic &= values[i] > values[i - 1];
        probe.Want("monotonic", monotonic, true);
        probe.Want(
            "ends-at-release-x",
            MathF.Abs(values[^1] - geo.ValueAt(DragFinalOffset)) < 1e-4f,
            true);
        // Reserve reports the press on the frame it lands, and the pointer has
        // been still for four frames before the release, so the whole trace
        // must sit inside the hold.
        probe.Want("no-report-before-press", frames[0] >= DragDownFrame, true);
        probe.Want("no-report-after-release", frames[^1] <= DragUpFrame, true);
        // Invariant: a decimal comma would not be the number the assertion
        // above compared, and this line is what a reviewer reads.
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"slider-drag reports={values.Count} first={values[0]:0.####} "
            + $"last={values[^1]:0.####} "
            + $"want-last={geo.ValueAt(DragFinalOffset):0.####} "
            + $"frames={frames[0]}..{frames[^1]}"));
        return probe;
    }

    /// <summary>The same gesture on a disabled track: the walk never even
    /// computes a value, so nothing is reported at all.</summary>
    private static Probe SliderDisabled(BehaviorHost host)
    {
        FormGeometry geo = formGeometry;
        int reports = 0;
        float value = 0.4f;
        var root = new UiRoot();
        host.Case(FormCanvas, 26, _ =>
        {
            ImGui.SetCursorScreenPos(FormOrigin);
            root.Render(
                FormOrigin,
                ImGui.GetContentRegionAvail(),
                () => new Slider
                {
                    Value = value,
                    Min = 0f,
                    Max = 1f,
                    OnChange = (Action<float>)(
                        next => { value = next; reports++; }),
                    Disabled = true,
                    StyleSheet = new()
                    {
                        Layout = new() { Width = UiDim.Fixed(200f) },
                    },
                });
        },
        frame => geo.TrackPoint(DragOffset(frame)),
        PressAt(DragDownFrame, DragUpFrame));

        var probe = new Probe();
        probe.Want("reports", reports, 0);
        probe.Want("value-untouched", value, 0.4f);
        return probe;
    }

    /// <summary>The toggle's contract: one click reports the NEGATION of what
    /// the element was showing, exactly once, and a disabled toggle reports
    /// nothing for the same gesture.</summary>
    private static Probe SwitchToggle(BehaviorHost host)
    {
        FormGeometry geo = formGeometry;
        var probe = new Probe();
        probe.Want("enabled", Drive(false), "1 True");
        // The same click on a toggle already showing True must report False:
        // the element reports the negation of what it SHOWS, not a stored flag.
        probe.Want("negates-shown", Drive(true), "1 False");
        probe.Want("disabled", Drive(false, disabled: true), "0 ");
        return probe;

        string Drive(bool shown, bool disabled = false)
        {
            int fired = 0;
            var trace = new List<bool>();
            var root = new UiRoot();
            host.Case(FormCanvas, 14, _ =>
            {
                ImGui.SetCursorScreenPos(FormOrigin);
                root.Render(
                    FormOrigin,
                    ImGui.GetContentRegionAvail(),
                    () => new Switch
                    {
                        Value = shown,
                        OnToggle = (Action<bool>)(
                            next => { fired++; trace.Add(next); }),
                        Disabled = disabled,
                    });
            },
            frame => frame is >= 1 and <= 6 ? geo.SwitchCenter : Offscreen,
            Presses(2));
            return $"{fired} {string.Join("|", trace)}";
        }
    }

    private static readonly Func<UiNode> FormColorWellTree = static () =>
        new ColorWell
        {
            Color = new Vector4(0.8f, 0.3f, 0.2f, 1f),
            OnChange = FormNoOpColor,
        };

    private static readonly Func<UiNode> FormColorWellDisabledTree =
        static () => new ColorWell
        {
            Color = new Vector4(0.8f, 0.3f, 0.2f, 1f),
            OnChange = FormNoOpColor,
            Disabled = true,
        };

    /// <summary>
    /// The well's popup, which the pixel sheet cannot reach: its handle is
    /// derived from the element PATH, so no fixture can name it and the open
    /// state has to be read off ImGui's own popup stack instead. That reads
    /// "some popup is up", and the tree is one well with nothing else that can
    /// open one — which the DISABLED run below turns from an argument into a
    /// control: same gesture, same tree, no popup.
    /// </summary>
    private static Probe ColorWellPopup(BehaviorHost host)
    {
        FormGeometry geo = formGeometry;
        var probe = new Probe();
        // Frames 6..12 observe the settled surface; the outside press lands at
        // 14 and frames 20..25 observe the dismissal.
        (int Held, int After) opened = Drive(FormColorWellTree);
        probe.Want("opens-on-click", opened.Held, 7);
        probe.Want("outside-dismisses", opened.After, 0);
        (int Held, int After) blocked = Drive(FormColorWellDisabledTree);
        probe.Want("disabled-never-opens", blocked.Held, 0);
        return probe;

        (int Held, int After) Drive(Func<UiNode> tree)
        {
            int held = 0;
            int after = 0;
            var root = new UiRoot();
            host.Case(FormCanvas, 26, frame =>
            {
                ImGui.SetCursorScreenPos(FormOrigin);
                root.Render(FormOrigin, ImGui.GetContentRegionAvail(), tree);
                // Sampled AFTER the walk: the surface is declared during it,
                // because the popup call is itself the open test.
                bool open = ImGui.IsPopupOpen(
                    string.Empty, ImGuiPopupFlags.AnyPopup);
                if (!open)
                    return;
                if (frame is >= 6 and <= 12)
                    held++;
                if (frame >= 20)
                    after++;
            },
            frame => frame is >= 1 and <= 4
                ? geo.WellCenter
                : frame is >= 13 and <= 17 ? FormOutside : Offscreen,
            PressAt(2, 4).Then(PressAt(14, 16)));
            return (held, after);
        }
    }

    /// <summary>Two press/release scripts on one timeline. Composed rather
    /// than written out so each gesture stays readable as a press and a
    /// release at named frames.</summary>
    private static Func<int, (bool HasEvent, bool Down)> Then(
        this Func<int, (bool HasEvent, bool Down)> first,
        Func<int, (bool HasEvent, bool Down)> second) =>
        frame => first(frame) is { HasEvent: true } hit ? hit : second(frame);

    /// <summary>The section's two plain content rows. Hoisted so the toggle
    /// case's extent numbers are arithmetic on the row token rather than on
    /// whatever a lambda happened to build.</summary>
    private const int SectionContentRows = 2;

    /// <summary>
    /// The disclosure's contract in three parts: the header reports the
    /// NEGATION once per click, the content rows are in the tree the very next
    /// frame (read off the root's own reserved extent, which is the only thing
    /// a row's presence changes), and the chevron keeps animating after the
    /// flip — proved by digesting the vertices the header emitted on two
    /// frames that are both EXPANDED, so the glyph swap cannot be what differs.
    /// </summary>
    private static Probe SectionToggle(BehaviorHost host)
    {
        var probe = new Probe();
        Theme.PageTokens page = default;
        float rowHeight = 0f;
        float scale = 1f;
        host.Case(FormCanvas, 1, _ =>
        {
            page = Ui.ActiveTheme.Page;
            rowHeight = Ui.ActiveTheme.Controls.FormRowHeight;
            scale = ImGuiHelpers.GlobalScale;
        }, _ => Offscreen);
        float collapsed = page.SectionMarginTop + 1f + page.SectionPaddingTop
            + page.SectionHeaderHeight;
        var headerCenter = new Vector2(
            FormOrigin.X + FormRowSize.X * scale * 0.5f,
            FormOrigin.Y
                + (page.SectionMarginTop + 1f + page.SectionPaddingTop
                    + page.SectionHeaderHeight * 0.5f) * scale);

        int fired = 0;
        var trace = new List<bool>();
        bool expanded = false;
        float beforeExtent = 0f;
        float afterExtent = 0f;
        var root = new UiRoot();
        host.Case(FormCanvas, 16, frame =>
        {
            ImGui.SetCursorScreenPos(FormOrigin);
            root.Render(
                FormOrigin,
                new Vector2(FormRowSize.X * scale, ImGui.GetContentRegionAvail().Y),
                () => new Section
                {
                    Title = "GENERAL",
                    Expanded = expanded,
                    OnExpandedChange = (Action<bool>)(next =>
                    {
                        fired++;
                        trace.Add(next);
                        expanded = next;
                    }),
                    Children =
                        [Rx.FormStatus("Row A"), Rx.FormStatus("Row B")],
                    Key = "section",
                });
            // Frame 1 is the settled collapsed extent; frame 3 is the frame
            // AFTER the press at 2, which is where the rows must have arrived.
            if (frame == 1)
                beforeExtent = ImGui.GetItemRectSize().Y;
            if (frame == 3)
                afterExtent = ImGui.GetItemRectSize().Y;
        },
        frame => frame is >= 1 and <= 6 ? headerCenter : Offscreen,
        Presses(2));

        probe.Want("reports-once", $"{fired} {string.Join("|", trace)}", "1 True");
        probe.Want("collapsed-extent", beforeExtent, collapsed * scale);
        probe.Want(
            "expanded-extent-next-frame", afterExtent,
            (collapsed + SectionContentRows * rowHeight) * scale);
        probe.Want(
            "chevron-motion",
            ChevronMotion(host, headerCenter, scale), string.Empty);
        return probe;
    }

    /// <summary>
    /// The chevron's motion, read off the vertices the header emitted rather
    /// than off a motion store this harness cannot name (the channel is keyed
    /// by the element's path-derived ImGui id). Both sampled frames are
    /// EXPANDED and the content is empty, so the only thing that can differ
    /// between them is the disclosure's own opacity — which is exactly what
    /// the 200ms transition is. The unclicked control run pins the other half:
    /// a section nothing touches emits the same vertices on both frames.
    /// </summary>
    private static string ChevronMotion(
        BehaviorHost host, Vector2 headerCenter, float scale)
    {
        ulong movedEarly = Digest(true, 5);
        ulong movedLate = Digest(true, 44);
        ulong stillEarly = Digest(false, 5);
        ulong stillLate = Digest(false, 44);
        if (movedEarly == movedLate)
            return "the chevron did not advance after the flip";
        return stillEarly == stillLate
            ? string.Empty
            : "an untouched header changed on its own";

        unsafe ulong Digest(bool click, int sample)
        {
            bool expanded = false;
            ulong digest = 0;
            var root = new UiRoot();
            host.Case(FormCanvas, sample + 1, frame =>
            {
                ImDrawListPtr list = ImGui.GetWindowDrawList();
                int start = list.VtxBuffer.Size;
                ImGui.SetCursorScreenPos(FormOrigin);
                root.Render(
                    FormOrigin,
                    new Vector2(
                        FormRowSize.X * scale,
                        ImGui.GetContentRegionAvail().Y),
                    () => new Section
                    {
                        Title = "GENERAL",
                        Expanded = expanded,
                        OnExpandedChange = (Action<bool>)(
                            next => expanded = next),
                        Children = UiChildren.Empty,
                        Key = "section",
                    });
                if (frame != sample)
                    return;
                int end = list.VtxBuffer.Size;
                var vertices = (ImDrawVert*)list.VtxBuffer.Data;
                ulong hash = 14695981039346656037UL;
                for (int i = start; i < end; i++)
                {
                    hash = Mix(hash, BitConverter.SingleToUInt32Bits(
                        vertices[i].Pos.X));
                    hash = Mix(hash, BitConverter.SingleToUInt32Bits(
                        vertices[i].Pos.Y));
                    hash = Mix(hash, vertices[i].Col);
                }
                digest = hash;
            },
            frame => click && frame is >= 1 and <= 6
                ? headerCenter
                : Offscreen,
            click ? Presses(2) : null);
            return digest;
        }

        static ulong Mix(ulong hash, uint word) =>
            (hash ^ word) * 1099511628211UL;
    }

    /// <summary>
    /// The readout's controlled round trip. The row builds its readout from
    /// the value the CALLER hands it, so mid-drag the string is one frame
    /// behind the value being reported — that lag is the contract, not a bug,
    /// and the assertion is therefore made on a settled post-release frame,
    /// where the caller's state has caught up and the two must agree exactly.
    /// </summary>
    private static Probe FormSliderReadout(BehaviorHost host)
    {
        var probe = new Probe();
        float thumbSpanStart = 0f;
        float rowMiddle = 0f;
        float pixel = 1f;
        host.Case(FormCanvas, 1, _ =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            pixel = scale;
            // The row's own arithmetic: a fixed label column, then the control
            // cell less the value column, and the track fills what is left —
            // inset by half a thumb, which is where the drag span starts.
            thumbSpanStart = FormOrigin.X
                + (Ui.ActiveTheme.Form.LabelColumnWidth
                    + Ui.ActiveTheme.Controls.SliderHeight * 0.5f) * scale;
            rowMiddle = FormOrigin.Y
                + Ui.ActiveTheme.Controls.FormRowHeight * 0.5f * scale;
        }, _ => Offscreen);

        float value = 0.4f;
        string readout = string.Empty;
        float lastReported = float.NaN;
        var root = new UiRoot();
        host.Case(FormCanvas, 26, _ =>
        {
            ImGui.SetCursorScreenPos(FormOrigin);
            // The row is built from the caller's value, so the string the row
            // shows is that value formatted — captured here, at build time,
            // which is the frame the row draws it.
            readout = value.ToString(
                "0.00", System.Globalization.CultureInfo.InvariantCulture);
            root.Render(
                FormOrigin,
                FormRowSize * ImGuiHelpers.GlobalScale,
                () => Rx.FormSlider(
                    "Weight", value, 0f, 1f,
                    (Action<float>)(
                        next => { value = next; lastReported = next; })));
        },
        frame => new Vector2(
            thumbSpanStart + DragOffset(frame) * pixel, rowMiddle),
        PressAt(DragDownFrame, DragUpFrame));

        probe.Want("dragged", !float.IsNaN(lastReported), true);
        if (float.IsNaN(lastReported))
            return probe;
        probe.Want("moved-off-start", lastReported != 0.4f, true);
        probe.Want(
            "readout-matches-final",
            readout,
            lastReported.ToString(
                "0.00", System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"form-slider-readout readout=\"{readout}\" "
            + $"final={lastReported:0.####}"));
        return probe;
    }

    /// <summary>
    /// The selector row's two inversions. The reset slot is PERMANENT, so an
    /// unowned row keeps the same trigger width and simply has nothing in the
    /// slot: a press there must reach neither callback. An owned row puts the
    /// button in it, and pressing it must reset WITHOUT selecting — the two
    /// live in the same cell and only their boxes keep them apart.
    /// </summary>
    private static Probe FormSelectorReset(BehaviorHost host)
    {
        var probe = new Probe();
        var resetPoint = default(Vector2);
        var triggerPoint = default(Vector2);
        host.Case(FormCanvas, 1, _ =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            float resetWidth = Rx.FormButtonWidth("Reset") * scale;
            float rowMiddle = FormOrigin.Y
                + Ui.ActiveTheme.Controls.FormRowHeight * 0.5f * scale;
            resetPoint = new Vector2(
                FormOrigin.X + FormRowSize.X * scale - resetWidth * 0.5f,
                rowMiddle);
            triggerPoint = new Vector2(
                FormOrigin.X
                    + (Ui.ActiveTheme.Form.LabelColumnWidth + 20f) * scale,
                rowMiddle);
        }, _ => Offscreen);

        probe.Want("unowned-slot-is-empty", Drive(false, resetPoint), "0 0");
        probe.Want("owned-slot-resets", Drive(true, resetPoint), "0 1");
        // The control: the same owned row, pressed on its TRIGGER, must select
        // and not reset — otherwise "0 1" above would prove only that presses
        // land somewhere.
        probe.Want("owned-trigger-selects", Drive(true, triggerPoint), "1 0");
        return probe;

        string Drive(bool owned, Vector2 point)
        {
            int selects = 0;
            int resets = 0;
            var root = new UiRoot();
            host.Case(FormCanvas, 14, _ =>
            {
                ImGui.SetCursorScreenPos(FormOrigin);
                root.Render(
                    FormOrigin,
                    FormRowSize * ImGuiHelpers.GlobalScale,
                    () => Rx.FormSelector(
                        "Model", "Date Modified",
                        (Action)(() => selects++), (Action)(() => resets++),
                        available: true, owned: owned));
            },
            frame => frame is >= 1 and <= 6 ? point : Offscreen,
            Presses(2));
            return $"{selects} {resets}";
        }
    }

    private static readonly Func<UiNode> FormRowTree = static () =>
        Rx.Page(
            new Section
            {
                Title = "Alloc",
                Expanded = true,
                OnExpandedChange = FormNoOpBool,
                Children =
                    Rx.FormSlider("Weight", 0.4f, 0f, 1f, FormNoOpFloat),
                Key = "alloc",
            });

    private static readonly Action<Ui.FormScope> LegacyFormRowBody =
        static form => form.Slider("Weight", 0.4f, 0f, 1f, FormNoOpFloat);

    private static readonly Action<Ui.PageScope> LegacyFormPageBody =
        static page => page.Section(
            "Alloc", true, FormNoOpBool, LegacyFormRowBody);

    /// <summary>
    /// The form row's warm-frame gate, held to the ceiling wave O measured
    /// rather than to parity. Two facts decide the shape. The retained runtime
    /// costs bytes PER DECLARED ELEMENT on every warm frame — a form row is a
    /// band, a label, a control cell, a track and a readout where the
    /// imperative row is one cursor and two draw calls — and that overhead is
    /// the RUNTIME's, not this row's. And the row builds its readout string
    /// every frame on both paths, so neither side is allocation-free to begin
    /// with. The gate is therefore the honest ceiling (3x) with BOTH numbers
    /// reported, which is what a reviewer needs and what a parity claim would
    /// have hidden.
    /// </summary>
    private static Probe FormRowAllocation(BehaviorHost host)
    {
        long reactive = MeasureFormRowAllocation(host, true);
        long legacy = MeasureFormRowAllocation(host, false);
        var probe = new Probe();
        if (reactive > legacy * 3)
            probe.Fault(
                $"a reactive form-slider row allocated {reactive} bytes over "
                + $"100 warm frames against the identical legacy row's "
                + $"{legacy} — past the 3x runtime-overhead ceiling");
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"allocation-form-row reactive={reactive} legacy={legacy} "
            + $"ratio={(legacy == 0 ? 0d : (double)reactive / legacy):0.00}"));
        return probe;
    }

    private static long MeasureFormRowAllocation(
        BehaviorHost host, bool reactive)
    {
        var root = new UiRoot();
        long allocated = 0;
        host.Case(FormCanvas, 120, frame =>
        {
            Vector2 size = FormCanvas - FormOrigin;
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImGui.SetCursorScreenPos(FormOrigin);
            if (reactive)
                root.Render(FormOrigin, size, FormRowTree);
            else
                Ui.Page("##alloc-form", FormOrigin, size, LegacyFormPageBody);
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
