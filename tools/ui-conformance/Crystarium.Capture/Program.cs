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

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: Crystarium.Capture <component> <output.png> [scale] [theme]");
            return 2;
        }

        string name = args[0];
        string output = Path.GetFullPath(args[1]);
        float scale = args.Length >= 3
            ? float.Parse(
                args[2],
                System.Globalization.CultureInfo.InvariantCulture)
            : 1f;
        if (scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        string themeName = args.Length >= 4 ? args[3] : "dark";
        Theme theme = ResolveTheme(themeName);
        Ui.UseTheme(theme);
        var component = ComponentCatalog.Get(name);
        int width = (int)MathF.Round(component.Width * scale);
        int height = (int)MathF.Round(component.Height * scale);

        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var form = new Form
        {
            Text = "Crystarium capture",
            ClientSize = new Size(width, height),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        form.Show();

        using var renderer = new Dx11Renderer();
        renderer.Initialize(form.Handle, width, height);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(width, height);
            io.DisplayFramebufferScale = Vector2.One;
            io.FontGlobalScale = scale;
            io.DeltaTime = 1f / 60f;
            io.IniFilename = null;
            ImGui.StyleColorsDark();

            using var fonts = new StandaloneFontAtlas(renderer);
            FontRegistry.Register(fonts);
            fonts.BuildFontsImmediately();
            if (!FontRegistry.Ready)
                throw new InvalidOperationException(
                    $"Font atlas is not ready: {FontRegistry.LastError}");

            // Covers HoverHelp's 400ms delay + 150ms entrance and every
            // shorter floating-surface transition before capture.
            const int frameCount = 40;
            for (int frame = 0; frame < frameCount; frame++)
            {
                Application.DoEvents();
                io.DeltaTime = 1f / 60f;
                io.DisplaySize = new Vector2(width, height);
                var pointer = ComponentCatalog.PointerFor(name, scale);
                io.AddMousePosEvent(pointer.X, pointer.Y);

                ImGui.NewFrame();
                Interactive.BeginFrame();
                ComponentCatalog.Draw(
                    name, frame, new Vector2(width, height));
                Ui.FloatingMenu.EndFrame();
                Ui.HoverHelp.Render();
                Interactive.EndFrame();
                ImGui.Render();

                renderer.BeginFrame(new Vector4(
                    theme.Surface.X,
                    theme.Surface.Y,
                    theme.Surface.Z,
                    1));
                renderer.Render(ImGui.GetDrawData());
                renderer.Present();
            }

            renderer.SaveBackbuffer(output);
            Console.WriteLine(output);
            return 0;
        }
        finally
        {
            FontRegistry.Dispose();
            ImGui.DestroyContext(context);
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
