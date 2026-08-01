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
            return BehaviorSuites.IconButton();

        if (args.Length == 1 && args[0] == "--reactive-button-behavior")
            return BehaviorSuites.ReactiveButton();

        if (args.Length == 1 && args[0] == "--kernel-behavior")
            return BehaviorSuites.Kernel();

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
                "       Crystarium.Capture --reactive-button-behavior\n" +
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
