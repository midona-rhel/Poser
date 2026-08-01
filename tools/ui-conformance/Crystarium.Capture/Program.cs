using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Dalamud.Bindings.ImGui;
using Poser.UI;
using Ui = Poser.UI.Crystarium;

namespace Crystarium.Capture;

internal static class Program
{
    [STAThread]
    private static unsafe int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--list")
        {
            foreach (var catalogItem in ComponentCatalog.All)
                Console.WriteLine(catalogItem.Name);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--measure")
            return Measure(float.Parse(
                args[1], System.Globalization.CultureInfo.InvariantCulture));

        if (args.Length == 1 && args[0] == "--icons")
        {
            // Ordered shipped-icon names; run.ps1 asserts the generated
            // reference renders exactly this list in this order.
            foreach (var name in Poser.UI.Tabler.ShippedNames())
                Console.WriteLine(name);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--fonts")
        {
            // The exact font files this machine resolves — base faces
            // plus the shared font-link CJK fallback. Provenance hashes
            // these paths instead of an assumed list.
            foreach (var file in FontRegistry.ResolveAllFiles())
                Console.WriteLine(file);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--icon-button-behavior")
            return RunIconButtonBehavior();

        if (args.Length == 1 && args[0] == "--kernel-behavior")
            return RunKernelBehavior();

        if (args.Length == 3 && args[0] == "--generate-tokens")
            return TokenEquality.Generate(args[1], args[2]);

        if (args.Length is 1 or 3 && args[0] == "--verify-tokens")
            return args.Length == 3
                ? TokenEquality.Verify(args[1], args[2])
                : TokenEquality.Verify(
                    DefaultTokensCssPath(), DefaultGeneratedPath());

        if (args.Length == 2 && args[0] == "--batch")
        {
            // One process for a whole capture list: the dominant cost of
            // a capture is process boot + D3D + atlas build, none of
            // which depend on component, theme, or scale.
            var entries = new List<BatchEntry>();
            foreach (var line in File.ReadAllLines(args[1]))
            {
                if (line.Length == 0)
                    continue;
                var parts = line.Split('\t');
                if (parts.Length != 4)
                    throw new FormatException(
                        $"Batch line needs name<TAB>output<TAB>scale<TAB>theme: '{line}'");
                entries.Add(new BatchEntry(
                    parts[0],
                    Path.GetFullPath(parts[1]),
                    float.Parse(
                        parts[2],
                        System.Globalization.CultureInfo.InvariantCulture),
                    parts[3]));
            }
            return RunCaptures(entries);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: Crystarium.Capture <component> <output.png> [scale] [theme]\n" +
                "       Crystarium.Capture --batch <listfile>\n" +
                "       Crystarium.Capture --measure <cssSize>\n" +
                "       Crystarium.Capture --icon-button-behavior\n" +
                "       Crystarium.Capture --kernel-behavior\n" +
                "       Crystarium.Capture --generate-tokens <tokens.css> <out.g.cs>\n" +
                "       Crystarium.Capture --verify-tokens [<tokens.css> <committed.g.cs>]\n" +
                "       Crystarium.Capture --list");
            return 2;
        }

        return RunCaptures(
        [
            new BatchEntry(
                args[0],
                Path.GetFullPath(args[1]),
                args.Length >= 3
                    ? float.Parse(
                        args[2],
                        System.Globalization.CultureInfo.InvariantCulture)
                    : 1f,
                args.Length >= 4 ? args[3] : "dark"),
        ]);
    }

    private readonly record struct BatchEntry(
        string Name, string Output, float Scale, string ThemeName);


    private static unsafe int RunCaptures(IReadOnlyList<BatchEntry> entries)
    {
        if (entries.Count == 0)
            return 0;
        int maxWidth = 0, maxHeight = 0;
        foreach (var entry in entries)
        {
            if (entry.Scale <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(entries), entry.Scale, "Scale must be positive.");
            ResolveTheme(entry.ThemeName);
            var spec = ComponentCatalog.Get(entry.Name);
            maxWidth = Math.Max(
                maxWidth, (int)MathF.Round(spec.Width * entry.Scale));
            maxHeight = Math.Max(
                maxHeight, (int)MathF.Round(spec.Height * entry.Scale));
        }

        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new Form
        {
            Text = "Crystarium capture",
            ClientSize = new Size(maxWidth, maxHeight),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();

        using var renderer = new Dx11Renderer();
        renderer.Initialize(form.Handle, maxWidth, maxHeight);
        // The root context exists to own the shared font atlas; every
        // entry renders in its OWN context created over that atlas, so
        // no ImGui interaction, timing, or widget state can survive from
        // one capture into the next while the expensive atlas build
        // still happens exactly once.
        var rootContext = ImGui.CreateContext();
        try
        {
            var rootIo = ImGui.GetIO();
            rootIo.IniFilename = null;
            Ui.UseTheme(ResolveTheme(entries[0].ThemeName));
            using var fonts = new StandaloneFontAtlas(renderer);
            var atlasClock = System.Diagnostics.Stopwatch.StartNew();
            FontRegistry.Register(fonts);
            fonts.BuildFontsImmediately();
            atlasClock.Stop();
            if (!FontRegistry.Ready)
                throw new InvalidOperationException(
                    $"Font atlas is not ready: {FontRegistry.LastError}");
            // First-visible-UI cost gate: the same atlas builds in game
            // before anything renders, so the number is watched here.
            Console.WriteLine(
                $"atlas-build-ms {atlasClock.ElapsedMilliseconds}");
            var sharedAtlas = ImGui.GetIO().Fonts;

            foreach (var entry in entries)
            {
                var theme = ResolveTheme(entry.ThemeName);
                Ui.UseTheme(theme);
                var spec = ComponentCatalog.Get(entry.Name);
                int width = (int)MathF.Round(spec.Width * entry.Scale);
                int height = (int)MathF.Round(spec.Height * entry.Scale);

                var entryContext = ImGui.CreateContext(sharedAtlas);
                ImGui.SetCurrentContext(entryContext);
                try
                {
                    var io = ImGui.GetIO();
                    io.DisplayFramebufferScale = Vector2.One;
                    io.FontGlobalScale = entry.Scale;
                    io.DeltaTime = 1f / 60f;
                    io.IniFilename = null;
                    // Keyboard-driven fixture states (focus-visible) Tab
                    // onto their control through real ImGui navigation.
                    io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                    ImGui.StyleColorsDark();

                    // Covers HoverHelp's 400ms delay + 150ms entrance and
                    // every shorter floating-surface transition.
                    const int frameCount = 40;
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        Application.DoEvents();
                        io.DeltaTime = 1f / 60f;
                        io.DisplaySize = new Vector2(width, height);
                        var pointer = ComponentCatalog.PointerFor(
                            entry.Name, entry.Scale, frame);
                        io.AddMousePosEvent(pointer.X, pointer.Y);
                        foreach (var (key, down) in
                            ComponentCatalog.KeyEventsFor(entry.Name, frame))
                            io.AddKeyEvent(key, down);
                        foreach (var (button, down) in
                            ComponentCatalog.MouseButtonEventsFor(
                                entry.Name, frame))
                            io.AddMouseButtonEvent(button, down);

                        ImGui.NewFrame();
                        Interactive.BeginFrame();
                        ComponentCatalog.Draw(
                            entry.Name, frame, new Vector2(width, height));
                        Ui.FloatingMenu.EndFrame();
                        Ui.HoverHelp.Render();
                        Interactive.EndFrame();
                        ImGui.Render();

                        // Intermediate frames advance real ImGui input and
                        // Crystarium animation state but are never observed.
                        // Submit the final TWO frames: after the second
                        // flip, SaveBackbuffer reads the same prior buffer
                        // as the legacy 40-present path (important for
                        // mid-transition fixtures) without 38 compositor
                        // waits.
                        if (frame >= frameCount - 2)
                        {
                            renderer.BeginFrame(new Vector4(
                                theme.Surface.X,
                                theme.Surface.Y,
                                theme.Surface.Z,
                                1));
                            renderer.Render(ImGui.GetDrawData());
                            renderer.Present();
                        }
                    }

                    renderer.SaveBackbuffer(entry.Output, width, height);
                    Console.WriteLine(entry.Output);
                }
                finally
                {
                    ImGui.SetCurrentContext(rootContext);
                    ImGui.DestroyContext(entryContext);
                }
            }
            return 0;
        }
        finally
        {
            FontRegistry.Dispose();
            ImGui.DestroyContext(rootContext);
        }
    }

    /// <summary>Metric probe for divergence investigations: prints the
    /// candidate's own prefix widths (tab-separated CSV per stdin line)
    /// for the given CSS size at scale 1, using the exact atlas and
    /// measurement path the captures use. Reference-side numbers come
    /// from the browser; comparing the two separates content divergence
    /// from rasterizer coverage without touching any fixture.</summary>
    private static unsafe int Measure(float size)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new Form
        {
            Text = "Crystarium measure",
            ClientSize = new Size(64, 64),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();
        using var renderer = new Dx11Renderer();
        renderer.Initialize(form.Handle, 64, 64);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(64, 64);
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = 1f;
            io.DeltaTime = 1f / 60f;
            io.IniFilename = null;
            using var fonts = new StandaloneFontAtlas(renderer);
            FontRegistry.Register(fonts);
            fonts.BuildFontsImmediately();
            if (!FontRegistry.Ready)
                throw new InvalidOperationException(
                    $"Font atlas is not ready: {FontRegistry.LastError}");
            ImGui.NewFrame();
            var style = new TextStyle { Size = size };
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Console.WriteLine(string.Create(
                invariant, $"ellipsis\t{Ui.MeasureText("…", style).X:0.####}"));
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;
                var widths = new System.Text.StringBuilder();
                for (int i = 1; i <= line.Length; i++)
                {
                    if (char.IsHighSurrogate(line[i - 1]))
                        continue;
                    if (widths.Length > 0)
                        widths.Append(',');
                    widths.Append(
                        Ui.MeasureText(line[..i], style).X.ToString(
                            "0.####", invariant));
                }
                Console.WriteLine(line + "\t" + widths);
            }
            ImGui.EndFrame();
            return 0;
        }
        finally
        {
            FontRegistry.Dispose();
            ImGui.DestroyContext(context);
        }
    }

    private readonly record struct BehaviorResult(
        int Activations, Vector2 Size);

    /// <summary>Real ImGui input sequences for the momentary action
    /// contract. Each case owns a fresh context over the shared atlas.</summary>
    private static unsafe int RunIconButtonBehavior()
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new Form
        {
            Text = "Crystarium icon-button behavior",
            ClientSize = new Size(500, 80),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();
        using var renderer = new Dx11Renderer();
        renderer.Initialize(form.Handle, 500, 80);
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
            var atlas = ImGui.GetIO().Fonts;
            var failures = new List<string>();

            Check("release-inside", 1, Run(
                atlas,
                pointer: frame => new Vector2(38, 38),
                mouse: frame => frame switch
                {
                    5 => (true, true),
                    7 => (true, false),
                    _ => default,
                }));
            Check("drag-release-outside", 0, Run(
                atlas,
                pointer: frame => frame < 6
                    ? new Vector2(38, 38)
                    : new Vector2(110, 70),
                mouse: frame => frame switch
                {
                    5 => (true, true),
                    7 => (true, false),
                    _ => default,
                }));
            Check("enter", 1, Run(
                atlas,
                pointer: _ => new Vector2(-1000, -1000),
                key: frame => frame switch
                {
                    2 => (true, ImGuiKey.Tab, true),
                    3 => (true, ImGuiKey.Tab, false),
                    6 => (true, ImGuiKey.Enter, true),
                    7 => (true, ImGuiKey.Enter, false),
                    _ => default,
                }));
            Check("space", 1, Run(
                atlas,
                pointer: _ => new Vector2(-1000, -1000),
                key: frame => frame switch
                {
                    2 => (true, ImGuiKey.Tab, true),
                    3 => (true, ImGuiKey.Tab, false),
                    6 => (true, ImGuiKey.Space, true),
                    7 => (true, ImGuiKey.Space, false),
                    _ => default,
                }));
            Check("disabled", 0, Run(
                atlas,
                pointer: _ => new Vector2(38, 38),
                mouse: frame => frame switch
                {
                    5 => (true, true),
                    7 => (true, false),
                    _ => default,
                },
                disabled: true));

            var defaultSize = Run(
                atlas,
                pointer: _ => new Vector2(-1000, -1000),
                canvasWidth: 500).Size;
            if (defaultSize != new Vector2(28f))
                failures.Add(
                    $"default-size: {defaultSize.X}x{defaultSize.Y}, want 28x28");
            var explicitSize = Run(
                atlas,
                pointer: _ => new Vector2(-1000, -1000),
                style: ControlStyle.Square(36f),
                canvasWidth: 500).Size;
            if (explicitSize != new Vector2(36f))
                failures.Add(
                    $"explicit-size: {explicitSize.X}x{explicitSize.Y}, want 36x36");

            if (failures.Count == 0)
            {
                Console.WriteLine(
                    "PASS release-inside=1 drag-release-outside=0 " +
                    "enter=1 space=1 disabled=0 default=28x28 explicit=36x36");
                return 0;
            }
            foreach (var failure in failures)
                Console.Error.WriteLine("FAIL " + failure);
            return 1;

            void Check(
                string name, int expected, BehaviorResult actual)
            {
                if (actual.Activations != expected)
                    failures.Add(
                        $"{name}: {actual.Activations}, want {expected}");
            }
        }
        finally
        {
            FontRegistry.Dispose();
            ImGui.DestroyContext(rootContext);
        }
    }

    private static unsafe BehaviorResult Run(
        ImFontAtlasPtr atlas,
        Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)>? mouse = null,
        Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
        bool disabled = false,
        ControlStyle style = default,
        int canvasWidth = 120)
    {
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = 1f;
            io.DeltaTime = 1f / 60f;
            io.DisplaySize = new Vector2(canvasWidth, 80);
            io.IniFilename = null;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();
            int activations = 0;
            Vector2 itemSize = default;
            for (int frame = 0; frame < 12; frame++)
            {
                io.AddMousePosEvent(pointer(frame).X, pointer(frame).Y);
                if (mouse?.Invoke(frame) is { HasEvent: true } m)
                    io.AddMouseButtonEvent(0, m.Down);
                if (key?.Invoke(frame) is { HasEvent: true } k)
                    io.AddKeyEvent(k.Key, k.Down);
                ImGui.NewFrame();
                Interactive.BeginFrame();
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(new Vector2(canvasWidth, 80));
                ImGui.PushStyleVar(
                    ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.Begin(
                    "##icon-button-behavior",
                    ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoBackground);
                ImGui.PopStyleVar();
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                Ui.IconButton(
                    TablerIcon.Settings,
                    () => activations++,
                    style,
                    disabled,
                    id: "##behavior-icon-button");
                itemSize = ImGui.GetItemRectSize();
                ImGui.End();
                Interactive.EndFrame();
                ImGui.Render();
            }
            return new BehaviorResult(activations, itemSize);
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    // ---- Kernel checks ----------------------------------------------
    //
    // The interaction kernel's guarantees are all about situations the
    // pixel fixtures cannot reach: a control under an open surface, a
    // drag that gets covered mid-flight, a transition with no duration,
    // a focus handover. Each case owns a fresh ImGui context over the
    // shared atlas and drives REAL input frames, the same way
    // RunIconButtonBehavior does.

    private const string KernelOccluderId = "##kernel-occluder";
    private const string KernelTargetId = "##kernel-target";
    private const int KernelCanvasWidth = 160;
    private const int KernelCanvasHeight = 80;
    // The reserved 28x28 control sits at (24,24); this point is inside it.
    private static readonly Vector2 KernelInside = new(38, 38);
    private static readonly Vector2 KernelOffscreen = new(-1000, -1000);

    private sealed class ReserveTally
    {
        public int Clicked;
        public int Activated;
        public int DragBegan;
        public int DragEnded;

        public override string ToString() =>
            $"clicked={Clicked} activated={Activated} " +
            $"began={DragBegan} ended={DragEnded}";
    }

    private static unsafe int RunKernelBehavior()
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new Form
        {
            Text = "Crystarium kernel behavior",
            ClientSize = new Size(KernelCanvasWidth, KernelCanvasHeight),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();
        using var renderer = new Dx11Renderer();
        renderer.Initialize(
            form.Handle, KernelCanvasWidth, KernelCanvasHeight);
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
            var atlas = ImGui.GetIO().Fonts;
            int failures = 0;

            // (a) A pointer press landing under a higher surface reports
            //     neither Clicked nor Activated; the same sequence with no
            //     occluder proves the press itself is real.
            var pointerBase = RunReserve(
                atlas,
                pointer: _ => KernelInside,
                mouse: PressAt(5, 7));
            var pointerOccluded = RunReserve(
                atlas,
                pointer: _ => KernelInside,
                mouse: PressAt(5, 7),
                occluded: _ => true);
            Report(
                "occluded-pointer",
                pointerBase.Clicked == 1 && pointerBase.Activated == 1
                    && pointerOccluded.Clicked == 0
                    && pointerOccluded.Activated == 0,
                $"occluded[{pointerOccluded}] baseline[{pointerBase}]");

            // (b) Keyboard focus with the pointer parked offscreen, so the
            //     POINTER gate cannot be what suppresses activation: only
            //     the rect gate can.
            var keyBase = RunReserve(
                atlas,
                pointer: _ => KernelOffscreen,
                key: TabThenEnterAndSpace);
            var keyOccluded = RunReserve(
                atlas,
                pointer: _ => KernelOffscreen,
                key: TabThenEnterAndSpace,
                occluded: _ => true);
            Report(
                "occluded-keyboard",
                keyBase.Activated == 2 && keyOccluded.Activated == 0,
                $"occluded[{keyOccluded}] baseline[{keyBase}]");

            // (c) A press swallowed by an occluder must not open a drag —
            //     and must not leave a dangling release behind either.
            var dragBase = RunReserve(
                atlas,
                pointer: frame => frame < 6
                    ? KernelInside
                    : KernelInside + new Vector2(10, 4),
                mouse: PressAt(4, 8));
            var dragOccluded = RunReserve(
                atlas,
                pointer: frame => frame < 6
                    ? KernelInside
                    : KernelInside + new Vector2(10, 4),
                mouse: PressAt(4, 8),
                occluded: _ => true);
            Report(
                "occluded-drag-begin",
                dragBase.DragBegan == 1 && dragBase.DragEnded == 1
                    && dragOccluded.DragBegan == 0
                    && dragOccluded.DragEnded == 0,
                $"occluded[{dragOccluded}] baseline[{dragBase}]");

            // (d) Ownership, not the current occlusion state, is what
            //     pairs the edges: a surface opening over a held control
            //     must not swallow the release.
            var midDrag = RunReserve(
                atlas,
                pointer: _ => KernelInside,
                mouse: PressAt(4, 8),
                occluded: frame => frame >= 6);
            Report(
                "drag-end-exactly-once",
                midDrag.DragBegan == 1 && midDrag.DragEnded == 1,
                midDrag.ToString());

            // (e)/(f) Motion's contract and its zero-duration snap.
            var contract = RunMotionContract(atlas);
            Report("motion-contract", contract.Length == 0, contract);
            var zero = RunMotionZeroDuration(atlas);
            Report("motion-zero-duration", zero.Length == 0, zero);

            // (g) Clearing is an edit of the field the user is in.
            var clear = RunTextInputClearFocus(atlas);
            Report("textinput-clear-focus", clear.Length == 0, clear);

            return failures == 0 ? 0 : 1;

            void Report(string name, bool ok, string detail)
            {
                if (ok)
                {
                    Console.WriteLine($"PASS {name} {detail}");
                    return;
                }
                failures++;
                Console.WriteLine($"FAIL {name} {detail}");
            }
        }
        finally
        {
            FontRegistry.Dispose();
            ImGui.DestroyContext(rootContext);
        }
    }

    private static Func<int, (bool, bool)> PressAt(int down, int up) =>
        frame => frame == down
            ? (true, true)
            : frame == up ? (true, false) : default;

    private static (bool, ImGuiKey, bool) TabThenEnterAndSpace(int frame) =>
        frame switch
        {
            2 => (true, ImGuiKey.Tab, true),
            3 => (true, ImGuiKey.Tab, false),
            6 => (true, ImGuiKey.Enter, true),
            7 => (true, ImGuiKey.Enter, false),
            10 => (true, ImGuiKey.Space, true),
            11 => (true, ImGuiKey.Space, false),
            _ => default,
        };

    /// <summary>One reserved control, optionally buried under a claimed
    /// exclusive surface, driven by real pointer/key frames.</summary>
    private static unsafe ReserveTally RunReserve(
        ImFontAtlasPtr atlas,
        Func<int, Vector2> pointer,
        Func<int, (bool HasEvent, bool Down)>? mouse = null,
        Func<int, (bool HasEvent, ImGuiKey Key, bool Down)>? key = null,
        Func<int, bool>? occluded = null,
        int frames = 16)
    {
        var tally = new ReserveTally();
        // Interaction ownership is process-wide state; no case may inherit
        // a chain link from the one before it.
        Interactive.ReleaseExclusive(KernelOccluderId);
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = 1f;
            io.DeltaTime = 1f / 60f;
            io.DisplaySize =
                new Vector2(KernelCanvasWidth, KernelCanvasHeight);
            io.IniFilename = null;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();
            for (int frame = 0; frame < frames; frame++)
            {
                io.AddMousePosEvent(pointer(frame).X, pointer(frame).Y);
                if (mouse?.Invoke(frame) is { HasEvent: true } m)
                    io.AddMouseButtonEvent(0, m.Down);
                if (key?.Invoke(frame) is { HasEvent: true } k)
                    io.AddKeyEvent(k.Key, k.Down);
                ImGui.NewFrame();
                Interactive.BeginFrame();
                // Registered BEFORE the control so the occluder is visible
                // to Reserve on the very frame it appears.
                if (occluded?.Invoke(frame) == true)
                {
                    if (!Interactive.OwnsExclusive(KernelOccluderId))
                        Interactive.ClaimExclusive(KernelOccluderId);
                    Interactive.EndOwner(Interactive.BeginOwner(
                        KernelOccluderId,
                        InteractionLayer.Popup,
                        Vector2.Zero,
                        new Vector2(
                            KernelCanvasWidth, KernelCanvasHeight)));
                }
                else
                {
                    Interactive.ReleaseExclusive(KernelOccluderId);
                }

                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(
                    new Vector2(KernelCanvasWidth, KernelCanvasHeight));
                ImGui.PushStyleVar(
                    ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.Begin(
                    "##kernel-behavior",
                    ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoBackground);
                ImGui.PopStyleVar();
                ImGui.SetCursorScreenPos(new Vector2(24, 24));
                var hit = Interactive.Reserve(
                    KernelTargetId,
                    new Vector2(28f),
                    disabled: false,
                    activateOnSpace: true);
                if (hit.Clicked) tally.Clicked++;
                if (hit.Activated) tally.Activated++;
                if (hit.DragBegan) tally.DragBegan++;
                if (hit.DragEnded) tally.DragEnded++;
                ImGui.End();
                Interactive.EndFrame();
                ImGui.Render();
            }
            return tally;
        }
        finally
        {
            Interactive.ReleaseExclusive(KernelOccluderId);
            ImGui.DestroyContext(context);
        }
    }

    /// <summary>Motion's channel-set contract. Needs a context only for
    /// the frame counter and delta; nothing is drawn.</summary>
    private static unsafe string RunMotionContract(ImFontAtlasPtr atlas)
    {
        const uint group = 0x4D0714A1;
        const uint fresh = 0x4D0714A2;
        var transition = Transition.CubicBezier(0.15f, 0.4f, 0f, 0.22f, 1f);
        var problems = new List<string>();
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.DisplaySize = new Vector2(64, 64);
            io.DeltaTime = 1f / 60f;
            io.IniFilename = null;

            ImGui.NewFrame();
            Expect("seed", false, group, Pair(0f, 0f));
            ImGui.Render();

            ImGui.NewFrame();
            Expect("dropped-channel", true, group, Lanes(0));
            Expect("duplicate-first-call", true, fresh, Lanes(0, 0));
            ImGui.Render();

            // The throw happens before anything mutates, so the stored
            // group is untouched: handing the full set back succeeds.
            ImGui.NewFrame();
            Expect("readd-after-throw", false, group, Pair(0f, 0f));
            ImGui.Render();

            ImGui.NewFrame();
            Expect("reordered-channels", true, group, Lanes(1, 0));
            ImGui.Render();

            ImGui.NewFrame();
            Expect("extra-channel", true, group, Lanes(0, 1, 2));
            ImGui.Render();

            ImGui.NewFrame();
            Expect("duplicate-on-stored-group", true, group, Lanes(0, 0));
            Expect("still-usable", false, group, Pair(0f, 0f));
            ImGui.Render();

            return problems.Count == 0
                ? string.Empty
                : string.Join("; ", problems);

            void Expect(
                string name, bool shouldThrow, uint id, MotionChannel[] set)
            {
                bool threw = false;
                try
                {
                    Motion.Toward(id, transition, set.AsSpan());
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                if (threw != shouldThrow)
                    problems.Add(
                        $"{name}: threw={threw}, want {shouldThrow}");
            }
        }
        finally
        {
            ImGui.DestroyContext(context);
        }

        static MotionChannel[] Lanes(params int[] channels)
        {
            var set = new MotionChannel[channels.Length];
            for (int i = 0; i < channels.Length; i++)
                set[i] = MotionChannel.Number(channels[i], 0f);
            return set;
        }

        static MotionChannel[] Pair(float a, float b) =>
            [MotionChannel.Number(0, a), MotionChannel.Number(1, b)];
    }

    /// <summary>A zero-duration transition has no clock to run, so it
    /// must arrive on the call that retargets it.</summary>
    private static unsafe string RunMotionZeroDuration(ImFontAtlasPtr atlas)
    {
        const uint retargeted = 0x4D0714B1;
        const uint seeded = 0x4D0714B2;
        var instant = new Transition(0f);
        var problems = new List<string>();
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.DisplaySize = new Vector2(64, 64);
            io.DeltaTime = 1f / 60f;
            io.IniFilename = null;

            ImGui.NewFrame();
            float first = Step(retargeted, 0f);
            float seedValue = Step(seeded, 5f);
            ImGui.Render();
            if (first != 0f)
                problems.Add($"seed: {first}, want 0");
            if (seedValue != 5f)
                problems.Add($"seed-nonzero: {seedValue}, want 5");

            ImGui.NewFrame();
            float snapped = Step(retargeted, 1f);
            ImGui.Render();
            if (snapped != 1f)
                problems.Add($"retarget: {snapped}, want 1 on the same call");

            ImGui.NewFrame();
            float held = Step(retargeted, 1f);
            ImGui.Render();
            if (held != 1f)
                problems.Add($"settled: {held}, want 1");

            return problems.Count == 0
                ? string.Empty
                : string.Join("; ", problems);

            float Step(uint id, float target)
            {
                var set = new[] { MotionChannel.Number(0, target) };
                Motion.Toward(id, instant, set.AsSpan());
                return set[0].Scalar;
            }
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    /// <summary>Clicking the clear affordance empties the field AND hands
    /// keyboard focus straight back to it.</summary>
    private static unsafe string RunTextInputClearFocus(ImFontAtlasPtr atlas)
    {
        var context = ImGui.CreateContext(atlas);
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = 1f;
            io.DeltaTime = 1f / 60f;
            io.DisplaySize = new Vector2(240, 80);
            io.IniFilename = null;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();

            string text = "hello";
            var clearTarget = KernelOffscreen;
            bool focused = false;
            for (int frame = 0; frame < 14; frame++)
            {
                io.AddMousePosEvent(
                    frame >= 2 ? clearTarget.X : KernelOffscreen.X,
                    frame >= 2 ? clearTarget.Y : KernelOffscreen.Y);
                if (frame == 4)
                    io.AddMouseButtonEvent(0, true);
                if (frame == 5)
                    io.AddMouseButtonEvent(0, false);
                ImGui.NewFrame();
                Interactive.BeginFrame();
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(new Vector2(240, 80));
                ImGui.PushStyleVar(
                    ImGuiStyleVar.WindowPadding, Vector2.Zero);
                ImGui.Begin(
                    "##kernel-textinput",
                    ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoBackground);
                ImGui.PopStyleVar();
                ImGui.SetCursorScreenPos(new Vector2(10, 20));
                Ui.ClearableTextInput(
                    "##kernel-clearable",
                    text,
                    next => text = next,
                    new ControlStyle { Width = UiWidth.Fixed(200) });
                // While the field still holds text the LAST submitted item
                // is the clear hit area, so its own rect is the click
                // target; once cleared it is the input again, whose focus
                // is what this case is about.
                if (text.Length > 0)
                    clearTarget = (ImGui.GetItemRectMin()
                        + ImGui.GetItemRectMax()) * 0.5f;
                else
                    focused = ImGui.IsItemFocused() || ImGui.IsItemActive();
                ImGui.End();
                Interactive.EndFrame();
                ImGui.Render();
            }
            var problems = new List<string>();
            if (text.Length != 0)
                problems.Add($"text='{text}', want empty");
            if (!focused)
                problems.Add("input did not regain focus");
            return problems.Count == 0
                ? string.Empty
                : string.Join("; ", problems);
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    // The Picto checkout is a sibling of the Poser repo; walking up from the
    // build output eventually reaches the folder that contains it. The ps1
    // wrappers pass explicit paths, so these are convenience fallbacks.
    private static string DefaultTokensCssPath() =>
        FindUpward("Picto/src/shared/styles/tokens.css");

    private static string DefaultGeneratedPath() =>
        FindUpward("Poser.UI/Rendering/PictoTokens.g.cs");

    private static string FindUpward(string rel)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null;
             dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate))
                return candidate;
        }
        return rel;
    }

    private static Theme ResolveTheme(string name) =>
        name.ToLowerInvariant() switch
        {
            "dark" => Theme.PictoDark,
            "light" => Theme.PictoLight,
            "lightgray" => Theme.PictoLightGray,
            "gray" => Theme.PictoGray,
            "blue" => Theme.PictoBlue,
            "purple" => Theme.PictoPurple,
            _ => throw new ArgumentException(
                $"Theme '{name}' is host-dependent or unknown."),
        };
}
