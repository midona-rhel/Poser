using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Diagnostics;
using Poser.Application.Settings;
using Poser.Domain;
using static Poser.UI.Widgets.ActionBarWidgets;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.WindowFrameWidgets;

namespace Poser.UI;

/// <summary>
/// The game function check: every function, hook, patch site, global and field offset
/// Poser depends on, and whether this game version still has it as Poser expects.
/// Lists unhealthy native functions before healthy ones. Its open state follows
/// the setting, like the frame profiler.
/// </summary>
public sealed class NativeHealthWindow : Window
{
    private const float DesignWidth = 640f;
    private const float DesignHeight = 620f;
    private const float RowHeight = 34f;
    private const float StateColumn = 70f;
    private const float ValueColumn = 130f;
    private const float TotalsHeight = 26f;
    private const float FootnoteHeight = 30f;
    private const float HeaderRowHeight = 20f;

    private static readonly string[] GroupOrder = ["Hooks", "Patch sites", "Functions", "Data", "Offsets", "Fields", "Not checkable"];

    private readonly ConfigurationService _configuration;
    private readonly INativeHealth _health;
    private NativeHealthReport? _report;
    private NativeHealthRow[] _rows = Array.Empty<NativeHealthRow>();
    private string _totals = "";
    private DateTime _readAt;
    private Action<ActionBarScope>? _footer;

    public NativeHealthWindow(ConfigurationService configuration, INativeHealth health)
        : base($"Game function check###{PluginConstants.PluginName}_nativehealth",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _configuration = configuration;
        _health = health;
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
        Refresh();
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var owner = Interactive.BeginOwner(
            "poser-native-health", InteractionLayer.Window, min, min + size);
        try
        {
            _footer ??= right => right.Button(
                "Check again",
                () =>
                {
                    _health.Recheck();
                    _readAt = default;
                },
                "Look up every game function and offset again");

            var rects = WindowFrame(
                "native-health",
                min,
                size,
                new WindowFrameProps
                {
                    Title = "Game function check",
                    OnClose = TurnOff,
                    CloseHelp = "Close the check",
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
        _configuration.Config.UI.ShowNativeCheck = false;
        _configuration.ApplyChange();
    }

    /// <summary>Hooks are read live, so the report is taken again each second, not
    /// every frame.</summary>
    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (_report != null && (now - _readAt).TotalSeconds < 1)
            return;
        _readAt = now;
        _report = _health.Report;
        _rows = _report.Rows
            .OrderBy(r => r.State)
            .ThenBy(r => Array.IndexOf(GroupOrder, r.Group))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var counts = _report.Rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        int Count(NativeHealthState state) => counts.GetValueOrDefault(state);
        _totals = _report.Complete
            ? $"{Count(NativeHealthState.Ok)} ok   {Count(NativeHealthState.Missing)} missing   "
                + $"{Count(NativeHealthState.Moved)} moved   {Count(NativeHealthState.Off)} off   "
                + $"{Count(NativeHealthState.Unchecked)} not checkable"
            : "Checking…";
    }

    private void DrawBody(WindowFrameRect body)
    {
        var theme = ActiveTheme;
        float s = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset * s;
        var left = new Vector2(body.Min.X + inset, body.Min.Y);
        float width = MathF.Max(1f, body.Size.X - inset * 2f);
        var dl = ImGui.GetWindowDrawList();

        TextInBand(
            left,
            new Vector2(width, TotalsHeight * s),
            _totals,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Family = FontFamily.Mono,
                Color = theme.Text,
            });

        var noteStyle = new TextStyle { Size = theme.Typography.CaptionSize, Color = theme.TextMuted };
        float noteY = left.Y + TotalsHeight * s;
        string checkedAt = _report is { Complete: true } report ? report.CheckedAt.ToString("HH:mm:ss") : "…";
        TextAt(new Vector2(left.X, noteY),
            $"Checked at {checkedAt}. Hooks are live: off means made but not running (its feature is idle).",
            noteStyle);
        TextAt(new Vector2(left.X, noteY + 13f * s),
            "Not checkable: Poser's own struct fields and constants the game's code has no pattern for.",
            noteStyle);

        float headerY = noteY + FootnoteHeight * s;
        var headerStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Weight = FontWeight.SemiBold,
            Color = theme.TextDim,
        };
        float state = StateColumn * s, value = ValueColumn * s;
        TextInBand(new Vector2(left.X, headerY), new Vector2(state, HeaderRowHeight * s), "STATE", headerStyle);
        TextInBand(new Vector2(left.X + state, headerY), new Vector2(width - state - value, HeaderRowHeight * s),
            "NAME", headerStyle);
        TextInBand(new Vector2(left.X + width - value, headerY), new Vector2(value, HeaderRowHeight * s),
            "VALUE", headerStyle, TextAlign.End);

        float ruleY = headerY + HeaderRowHeight * s;
        dl.AddRectFilled(
            new Vector2(left.X, ruleY),
            new Vector2(left.X + width, ruleY + MathF.Max(1f, s)),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.FormSeparator)));

        float listTop = ruleY + theme.Spacing.Two * s;
        ImGui.SetCursorScreenPos(new Vector2(body.Min.X, listTop));
        ScrollRegion(
            "##native-health-rows",
            body.Size.X / s,
            MathF.Max(1f, (body.Max.Y - listTop) / s),
            DrawRows);
    }

    private void DrawRows(ScrollRegionScope region)
    {
        var theme = ActiveTheme;
        float s = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset * s;
        var origin = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, region.ContentWidth * s - inset * 2f);
        float x = origin.X + inset;
        float y = origin.Y;
        float state = StateColumn * s, value = ValueColumn * s;
        float line = RowHeight * s / 2f;

        var nameStyle = new TextStyle { Size = theme.Typography.LabelSize, Color = theme.Text };
        var valueStyle = new TextStyle { Size = theme.Typography.CaptionSize, Family = FontFamily.Mono, Color = theme.Text };
        var detailStyle = new TextStyle { Size = theme.Typography.CaptionSize, Color = theme.TextMuted };

        foreach (var row in _rows)
        {
            var stateStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Weight = FontWeight.SemiBold,
                Color = row.State switch
                {
                    NativeHealthState.Missing => theme.Danger,
                    NativeHealthState.Moved => theme.Warning,
                    NativeHealthState.Ok => theme.Success,
                    NativeHealthState.Off => theme.TextMuted,
                    _ => theme.TextDim,
                },
            };
            TextInBand(new Vector2(x, y), new Vector2(state, line), StateWord(row.State), stateStyle);
            float nameWidth = MathF.Max(1f, width - state - value);
            TextInBand(new Vector2(x + state, y), new Vector2(nameWidth, line), row.Name, nameStyle,
                TextConstraint.Truncate(nameWidth, TextAlign.Start));
            TextInBand(new Vector2(x + width - value, y), new Vector2(value, line), row.Value, valueStyle,
                TextAlign.End);
            float detailWidth = MathF.Max(1f, width - state);
            string detail = string.IsNullOrEmpty(row.UsedBy)
                ? $"{row.Group} · {row.Detail}"
                : $"{row.Group} · {row.Detail} · used by {row.UsedBy}";
            TextInBand(new Vector2(x + state, y + line), new Vector2(detailWidth, line), detail, detailStyle,
                TextConstraint.Truncate(detailWidth, TextAlign.Start));
            y += RowHeight * s;
        }

        if (_rows.Length == 0)
            TextAt(new Vector2(x, y + theme.Spacing.Two * s), "Checking…",
                new TextStyle { Size = theme.Typography.LabelSize, Color = theme.FormHint });

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
        ImGui.Dummy(new Vector2(1f, 1f));
    }

    private static string StateWord(NativeHealthState state) => state switch
    {
        NativeHealthState.Missing => "MISSING",
        NativeHealthState.Moved => "MOVED",
        NativeHealthState.Off => "OFF",
        NativeHealthState.Ok => "OK",
        _ => "—",
    };
}
