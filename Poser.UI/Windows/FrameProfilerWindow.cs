using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Config;

namespace Poser.UI;

/// <summary>
/// THE PERF panel: every labelled draw unit's cost, worst first.
///
/// <para>It reads <see cref="FrameProfiler"/>'s published figures, which are
/// last frame's — the ledger closes after every window has drawn, this one
/// included, so a panel that read the live accumulators would be reading a
/// half-built frame and would never account for itself.</para>
///
/// <para>The panel's OWN cost is measured like everything else, under
/// <c>Window · Frame profiler</c>: a diagnostic surface that hides its own
/// price is a diagnostic surface that lies. It formats a few dozen numbers per
/// frame and that shows up there.</para>
///
/// <para>Closing it turns the setting off rather than leaving a switch that
/// claims the panel is showing — the setting IS whether the panel is up.
/// </para>
/// </summary>
public sealed class FrameProfilerWindow : Window
{
    private const float DesignWidth = 470f;
    private const float DesignHeight = 540f;

    /// <summary>Rows below this smoothed self-cost are not drawn: a label that
    /// has decayed to nothing is a surface that is no longer on screen, and
    /// forty such rows would bury the ones that matter.</summary>
    private const double FloorMs = 0.0005;

    private const float RowHeight = 20f;
    private const float NumberColumn = 66f;
    private const float TotalsHeight = 26f;
    private const float FootnoteHeight = 30f;
    private const float HeaderRowHeight = 20f;

    private readonly ConfigurationService _configuration;

    // Reused across frames: reading the ledger must not allocate a buffer per
    // frame, which is the whole reason Snapshot fills one the caller owns.
    private FrameProfiler.Sample[] _samples = new FrameProfiler.Sample[64];
    private double[] _keys = Array.Empty<double>();
    private int[] _order = Array.Empty<int>();

    private Action<Crystarium.ActionBarScope>? _footer;

    public FrameProfilerWindow(ConfigurationService configuration)
        : base($"Frame profiler###{PluginConstants.PluginName}_frameprofiler",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _configuration = configuration;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        Size = new Vector2(DesignWidth, DesignHeight);
        SizeCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        using var _ = FrameProfiler.Scope("Window · Frame profiler");
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var owner = Interactive.BeginOwner(
            "poser-frame-profiler", InteractionLayer.Window, min, min + size);
        try
        {
            _footer ??= right => right.Button(
                "Reset peaks",
                FrameProfiler.ResetPeaks,
                "Clear the worst-frame column and start watching again");

            var rects = Crystarium.WindowFrame(
                "frame-profiler",
                min,
                size,
                new WindowFrameProps
                {
                    Title = "Frame profiler",
                    OnClose = TurnOff,
                    CloseHelp = "Stop profiling and close",
                    FooterRight = _footer,
                });
            DrawBody(rects.Body);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private void TurnOff()
    {
        _configuration.Config.UI.ShowFrameProfiler = false;
        _configuration.ApplyChange();
    }

    private void DrawBody(WindowFrameRect body)
    {
        var theme = Crystarium.ActiveTheme;
        float s = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset * s;
        var left = new Vector2(body.Min.X + inset, body.Min.Y);
        float width = MathF.Max(1f, body.Size.X - inset * 2f);
        var dl = ImGui.GetWindowDrawList();

        var totalStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Weight = FontWeight.SemiBold,
            Family = FontFamily.Mono,
            Color = theme.Text,
        };
        Crystarium.TextInBand(
            new Vector2(left.X, left.Y),
            new Vector2(width, TotalsHeight * s),
            Milliseconds(FrameProfiler.AverageFrameMs) + " ms avg   "
                + Milliseconds(FrameProfiler.PeakFrameMs) + " ms peak",
            totalStyle);

        var noteStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.TextMuted,
        };
        float noteY = left.Y + TotalsHeight * s;
        Crystarium.TextAt(
            new Vector2(left.X, noteY),
            "CPU inside the draw callback only. The backdrop blur's GPU cost is",
            noteStyle);
        Crystarium.TextAt(
            new Vector2(left.X, noteY + 13f * s),
            "submitted here and executed later — it is invisible to these numbers.",
            noteStyle);

        float headerY = noteY + FootnoteHeight * s;
        var headerStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Weight = FontWeight.SemiBold,
            Color = theme.TextDim,
        };
        float column = NumberColumn * s;
        var headerBand = new Vector2(0f, HeaderRowHeight * s);
        Crystarium.TextInBand(
            new Vector2(left.X, headerY),
            new Vector2(width - column * 3f, HeaderRowHeight * s),
            "DRAW UNIT", headerStyle);
        Crystarium.TextInBand(
            new Vector2(left.X + width - column * 3f, headerY),
            new Vector2(column, headerBand.Y),
            "self", headerStyle, TextAlign.End);
        Crystarium.TextInBand(
            new Vector2(left.X + width - column * 2f, headerY),
            new Vector2(column, headerBand.Y),
            "peak", headerStyle, TextAlign.End);
        Crystarium.TextInBand(
            new Vector2(left.X + width - column, headerY),
            new Vector2(column, headerBand.Y),
            "incl", headerStyle, TextAlign.End);

        float ruleY = headerY + HeaderRowHeight * s;
        dl.AddRectFilled(
            new Vector2(left.X, ruleY),
            new Vector2(left.X + width, ruleY + MathF.Max(1f, s)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));

        int count = Rank();
        float listTop = ruleY + theme.Spacing.Two * s;
        ImGui.SetCursorScreenPos(new Vector2(body.Min.X, listTop));
        Crystarium.ScrollRegion(
            "##frame-profiler-rows",
            body.Size.X / s,
            MathF.Max(1f, (body.Max.Y - listTop) / s),
            region => DrawRows(region, count, column));
    }

    /// <summary>Sorts the published labels by self cost, worst first, into the
    /// reused order buffer. <c>Array.Sort</c> over two arrays the panel owns
    /// allocates nothing.</summary>
    private int Rank()
    {
        if (_samples.Length < FrameProfiler.LabelCount)
            _samples = new FrameProfiler.Sample[
                Math.Max(FrameProfiler.LabelCount, _samples.Length * 2)];
        int count = FrameProfiler.Snapshot(_samples);
        if (_keys.Length < count)
        {
            _keys = new double[_samples.Length];
            _order = new int[_samples.Length];
        }
        for (int i = 0; i < count; i++)
        {
            // Negated so the ascending sort lands the worst row first.
            _keys[i] = -_samples[i].AverageSelfMs;
            _order[i] = i;
        }
        Array.Sort(_keys, _order, 0, count);
        return count;
    }

    private void DrawRows(
        Crystarium.ScrollRegionScope region, int count, float column)
    {
        var theme = Crystarium.ActiveTheme;
        float s = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset * s;
        var origin = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, region.ContentWidth * s - inset * 2f);
        float x = origin.X + inset;
        float y = origin.Y;

        var labelStyle = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Color = theme.Text,
        };
        var valueStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Color = theme.Text,
        };
        var dimStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Color = theme.TextMuted,
        };

        int drawn = 0;
        var band = new Vector2(column, RowHeight * s);
        for (int i = 0; i < count; i++)
        {
            var sample = _samples[_order[i]];
            if (sample.AverageSelfMs < FloorMs && sample.Hits == 0)
                continue;
            float labelWidth = MathF.Max(1f, width - column * 3f);
            Crystarium.TextInBand(
                new Vector2(x, y),
                new Vector2(labelWidth, RowHeight * s),
                sample.Label,
                labelStyle,
                TextConstraint.Truncate(labelWidth, TextAlign.Start));
            Crystarium.TextInBand(
                new Vector2(x + width - column * 3f, y), band,
                Milliseconds(sample.AverageSelfMs), valueStyle, TextAlign.End);
            Crystarium.TextInBand(
                new Vector2(x + width - column * 2f, y), band,
                Milliseconds(sample.PeakSelfMs), dimStyle, TextAlign.End);
            Crystarium.TextInBand(
                new Vector2(x + width - column, y), band,
                Milliseconds(sample.AverageInclusiveMs), dimStyle,
                TextAlign.End);
            y += RowHeight * s;
            drawn++;
        }

        if (drawn == 0)
            Crystarium.TextAt(
                new Vector2(x, y + theme.Spacing.Two * s),
                "Nothing measured yet.",
                new TextStyle
                {
                    Size = theme.Typography.LabelSize,
                    Color = theme.FormHint,
                });

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
        ImGui.Dummy(new Vector2(1f, 1f));
    }

    private static string Milliseconds(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);
}
