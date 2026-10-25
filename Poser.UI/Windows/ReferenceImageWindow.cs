using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Widgets;
using Poser.Documents.Config;
using static Poser.UI.Widgets.SliderWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.WindowFrameWidgets;

namespace Poser.UI;

/// <summary>
/// Displays a reference image edge to edge in a floating window. Zero padding
/// lets the window size use the image aspect ratio directly. Image and border
/// share the saved opacity, with no background or shadow hiding the game.
/// The title bar overlays the image on hover and keeps a separate fade so its
/// controls remain readable. Loading and failure states use normal window chrome.
/// </summary>
public sealed class ReferenceImageWindow : Window
{
    /// <summary>Logical width of the opacity track in the bar.</summary>
    private const float OpacityTrackWidth = 96f;

    /// <summary>Logical width of the percent readout beside the track.
    /// </summary>
    private const float OpacityReadoutWidth = 38f;

    private readonly ReferenceImageSession _session;
    private readonly ReferenceImageInstance _image;
    private readonly string _ownerId;
    private readonly string _frameId;
    private readonly string _opacityId;
    private readonly Action<WindowFrameRect> _titleContent;
    private readonly Action _close;

    /// <summary>The last applied size in logical pixels — the
    /// reference <see cref="ReferenceImageGeometry.ResolveAspect"/> measures
    /// the user's drift against, used to correct its aspect ratio.
    /// </summary>
    private Vector2 _applied;

    /// <summary>What ImGui reported last frame, logical. PreDraw runs before
    /// Draw, so the correction it applies is always against the previous
    /// frame's observation.</summary>
    private Vector2 _observed;

    /// <summary>0..1 raw ramp behind the title bar's fade.</summary>
    private float _barRamp;

    /// <summary>An initial size has been applied from stored placement or the
    /// picture's own pixels. Until then the window uses a placeholder and
    /// does not save placement, or the placeholder would become the stored
    /// placement on the next frame.</summary>
    private bool _seated;

    /// <summary>The one-frame <see cref="Window.Position"/> write is still
    /// pending. Dalamud re-applies a non-null Position every frame, so an
    /// uncleared Always position prevents dragging.</summary>
    private bool _positionApplied;

    private bool _hovered;

    /// <summary>The ratio <see cref="ConformToAspect"/> is to hold, handed over
    /// for the length of one <c>Begin</c>. Static because the callback is a
    /// plain function with no state of its own, and only ever runs between the
    /// <c>SetNextWindowSizeConstraints</c> that arms it and the <c>Begin</c>
    /// immediately after — one window, one thread, no overlap.</summary>
    private static float _constraintAspect;

    /// <summary>
    /// Preserves the image ratio during resizing. Edge drags preserve the moved
    /// axis; corner drags project the desired size onto the aspect-ratio line.
    /// </summary>
    private static unsafe void ConformToAspect(ImGuiSizeCallbackData* data)
    {
        float aspect = _constraintAspect;
        if (aspect <= 0f || data == null)
            return;
        var current = data->CurrentSize;
        var desired = data->DesiredSize;
        float dx = desired.X - current.X;
        float dy = desired.Y - current.Y;
        // A CORNER drag moves both axes, and their deltas trade places from
        // frame to frame; choosing a dominant axis each frame flipped the
        // derived side back and forth — the flicker. The corner takes the
        // closest on-ratio size to what the hand asked for, a projection
        // onto the aspect line that is smooth in both deltas. An EDGE drag
        // moves one axis and keeps that axis authoritative.
        if (MathF.Abs(dx) > 0.5f && MathF.Abs(dy) > 0.5f)
        {
            float width = (desired.X * aspect * aspect + desired.Y * aspect)
                / (aspect * aspect + 1f);
            data->DesiredSize = new Vector2(width, width / aspect);
            return;
        }
        data->DesiredSize =
            MathF.Abs(dx) >= MathF.Abs(dy)
                ? new Vector2(desired.X, desired.X / aspect)
                : new Vector2(desired.Y * aspect, desired.Y);
    }

    /// <summary>The ImGui window name for one picture. Stated here because the
    /// window owns its own naming: the sidebar row raises a picture by asking
    /// ImGui for this name, and a second spelling of it elsewhere would be a
    /// raise that silently found nothing.</summary>
    public static string WindowNameFor(ReferenceImageInstance image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return $"###poser_reference_image_{image.Id}";
    }

    public ReferenceImageWindow(
        ReferenceImageSession session, ReferenceImageInstance image)
        : base(WindowNameFor(image),
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings)
    {
        _session = session;
        _image = image;
        _ownerId = $"poser-reference-image-{image.Id}";
        _frameId = $"reference-image-{image.Id}";
        _opacityId = $"##reference-image-opacity-{image.Id}";
        _titleContent = DrawTitleContent;
        _close = () => _session.Close(_image);
        // ImGui's own ini is deliberately not the store: the roster is config
        // (see ReferenceImageConfiguration), so placement restores with the
        // picture rather than with a window id.
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public ReferenceImageInstance Image => _image;

    public override unsafe void PreDraw()
    {
        base.PreDraw();

        var entry = _image.Entry;
        float aspect = _image.Aspect;
        // Hoisted: the size seats below and the constraint at the end both
        // measure in it, and the constraint is stated in PHYSICAL pixels
        // because ImGui's own is — Dalamud's SizeConstraints was the only
        // thing scaling that for us, and it is off for this window now.
        float gs = MathF.Max(ImGuiHelpers.GlobalScale, 0.01f);

        // The Always position seat lasts exactly one frame — see
        // _positionApplied.
        if (_positionApplied)
        {
            Position = null;
            _positionApplied = false;
        }

        // First seating: the stored placement if there is one, otherwise the
        // picture's own pixels fitted into the viewport once they arrive.
        if (!_seated)
        {
            if (entry.Width > 0f && entry.Height > 0f)
            {
                _applied = new Vector2(entry.Width, entry.Height);
                // Dalamud's Window.Size is LOGICAL and its Position is SCREEN
                // PIXELS (SplitShellWindows.PlaceAt states the same split);
                // the roster stores both logical, so only the position scales.
                Position = new Vector2(entry.X, entry.Y)
                    * ImGuiHelpers.GlobalScale;
                PositionCondition = ImGuiCond.Always;
                _positionApplied = true;
                Size = _applied;
                SizeCondition = ImGuiCond.Always;
                _observed = _applied;
                _seated = true;
            }
            else if (aspect > 0f)
            {
                _applied = ReferenceImageGeometry.InitialSize(
                    _image.Pixels / gs, ImGui.GetIO().DisplaySize / gs);
                Size = _applied;
                SizeCondition = ImGuiCond.Always;
                _observed = _applied;
                _seated = true;
            }
            else
            {
                // No picture and nothing stored: the empty state needs a box
                // to stand in, and nothing is written back until a real seat
                // replaces it.
                Size = new Vector2(
                    ReferenceImageGeometry.MinimumSide * 2f,
                    ReferenceImageGeometry.MinimumSide * 1.5f);
                SizeCondition = ImGuiCond.FirstUseEver;
            }
        }
        else
        {
            var target = ReferenceImageGeometry.ResolveAspect(
                _applied, _observed, aspect);
            _applied = target;
            // Written only while the observation is genuinely off-ratio: a
            // conformant window leaves SetNextWindowSize alone so the resize
            // grip keeps its own authority. And never while the button is
            // down: the size constraint callback owns the ratio during a
            // drag, and a correction on the same frame fought it — a corner
            // pull sized twice and flickered. The seat is corrected once
            // the hand lets go.
            bool dragging = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            if (!dragging
                && (MathF.Abs(target.X - _observed.X) > 0.5f
                    || MathF.Abs(target.Y - _observed.Y) > 0.5f))
            {
                Size = target;
                SizeCondition = ImGuiCond.Always;
            }
            else
            {
                SizeCondition = ImGuiCond.FirstUseEver;
            }
        }

        // The ratio is held as a CONSTRAINT, not as a correction. Dalamud's
        // SizeConstraints is left null on purpose so it issues no competing
        // call: this one carries the aspect callback, and ImGui runs it inside
        // the Begin that follows this method, while the resize is being
        // resolved. The post-hoc ResolveAspect above still governs the
        // programmatic seats (a restored placement, the picture's own pixels);
        // what it cannot govern is the DRAG, because it only ever sees last
        // frame's size — so every dragged frame was submitted off-ratio and
        // snapped back on the next one, which is the deformation the user saw.
        SizeConstraints = null;
        // The callback reads this rather than a capture: it fires inside the
        // very next Begin, on this thread, for this window, so a static hand-off
        // is the whole lifetime it needs and the delegate stays allocation-free.
        _constraintAspect = aspect;
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(ReferenceImageGeometry.MinimumSide * gs),
            new Vector2(float.MaxValue, float.MaxValue),
            ConformToAspect,
            null);

        ResizeAccent.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding,
            ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ResizeAccent.Pop();
        base.PostDraw();
    }

    public override void Draw()
    {
        float scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var max = min + size;
        _observed = size / MathF.Max(scale, 0.01f);
        // Only a seated window states where it is: writing the placeholder box
        // back would make it the stored placement and the picture would never
        // reach its own size.
        if (_seated)
            _session.SetPlacement(
                _image, min / MathF.Max(scale, 0.01f), _observed);

        var theme = ActiveTheme;
        var draw = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            _ownerId, InteractionLayer.FloatingWindow, min, max);
        try
        {
            if (_image.Handle != 0)
                DrawPicture(draw, min, max, scale);
            else
            {
                // Nothing to see through: the empty state stands on the full
                // chassis.
                FloatingSurface.DrawChrome(
                    draw, min, max, theme.Radii.Window);
                DrawEmptyState(min, size);
            }

            DrawTitleBar(min, size, scale, draw);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    /// <summary>
    /// Draws the image and border with the same saved opacity. Background,
    /// blur, and shadow are disabled so the game remains visible through it.
    /// </summary>
    private void DrawPicture(
        ImDrawListPtr draw, Vector2 min, Vector2 max, float scale)
    {
        var theme = ActiveTheme;
        float opacity = _image.Entry.Opacity;
        draw.AddImageRounded(
            new ImTextureID(_image.Handle),
            min,
            max,
            Vector2.Zero,
            Vector2.One,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, opacity))),
            theme.Radii.Window * scale);

        ImGui.PushStyleVar(
            ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * opacity);
        try
        {
            FloatingSurface.DrawChrome(
                draw, min, max, theme.Radii.Window,
                shadow: false,
                blur: false,
                fill: false);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>Absence explained IN PLACE, in the words the instance carries
    /// — a loading picture and a missing one are different absences.</summary>
    private void DrawEmptyState(Vector2 min, Vector2 size)
    {
        TextInBand(
            min,
            size,
            _image.Failure ?? "Reading the picture…",
            new TextStyle
            {
                Size = ActiveTheme.Typography.CaptionSize,
                Color = ActiveTheme.FormHint,
            },
            TextAlign.Center);
    }

    /// <summary>
    /// The bar, faded in over the picture. Hover is sampled with
    /// <see cref="ImGuiHoveredFlags.AllowWhenBlockedByActiveItem"/> and held
    /// open while one of the bar's own controls is being dragged, so pulling
    /// the opacity slider past the window's edge does not dismiss the bar
    /// under the pointer.
    /// </summary>
    private void DrawTitleBar(
        Vector2 min, Vector2 size, float scale, ImDrawListPtr draw)
    {
        _hovered = ImGui.IsWindowHovered(
                ImGuiHoveredFlags.AllowWhenBlockedByActiveItem
                | ImGuiHoveredFlags.ChildWindows)
            || (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                && ImGui.IsAnyItemActive());

        _barRamp = Math.Clamp(
            _barRamp
                + (_hovered ? 1f : -1f) * ImGui.GetIO().DeltaTime
                    / Transition.EaseNormal.DurationSeconds,
            0f,
            1f);
        float fade = Transition.EaseNormal.Evaluate(_barRamp);
        // Submitted only while it is visible: a bar at zero alpha still
        // reserves its close button, and an invisible close is a trap.
        if (fade <= 0.001f)
            return;

        var theme = ActiveTheme;
        float barHeight = theme.Floating.ModalBarHeight * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, fade);
        try
        {
            // The bar's own ground. Every other Poser title bar stands on the
            // window's glass fill; this window has none under it, so the band
            // lays that same fill for itself, rounded into the top corners.
            draw.AddRectFilled(
                min,
                new Vector2(min.X + size.X, min.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(
                        FloatingSurface.FillColor)),
                theme.Radii.Window * scale,
                ImDrawFlags.RoundCornersTop);

            WindowFrame(
                _frameId,
                min,
                size,
                new WindowFrameProps
                {
                    Title = _image.Name,
                    TitleContent = _titleContent,
                    OnClose = _close,
                    CloseHelp = "Close this reference image",
                    // The window drew its own frame above, so this frame must
                    // not lay a second one over the picture.
                    HostPaintsChrome = true,
                });
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>
    /// The bar's content: the picture's name on the left, then the opacity
    /// track and its readout, sized to leave the frame's own close cluster its
    /// room (the same rule <c>SpawnBrowserView.DrawSearchInTitle</c> follows).
    /// </summary>
    private void DrawTitleContent(WindowFrameRect bar)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Floating.HeaderInset * scale;
        float gap = theme.Page.ActionGap * scale;
        float cluster = theme.Floating.CloseActionSize * scale + gap;
        float track = OpacityTrackWidth * scale;
        float readout = OpacityReadoutWidth * scale;

        float trackRight = bar.Max.X - inset - cluster;
        float trackLeft = trackRight - readout - gap - track;
        float labelWidth = trackLeft - gap - (bar.Min.X + inset);

        var labelStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Weight = FontWeight.SemiBold,
            Color = theme.Chrome.Text,
        };
        if (labelWidth > 0f)
            TextInBand(
                new Vector2(bar.Min.X + inset, bar.Min.Y),
                new Vector2(labelWidth, bar.Size.Y),
                _image.Name,
                labelStyle,
                MeasureText(_image.Name, labelStyle).X > labelWidth
                    ? TextConstraint.Truncate(labelWidth)
                    : TextConstraint.Intrinsic);

        float trackHeight = theme.Controls.SliderHeight * scale;
        ImGui.SetCursorScreenPos(new Vector2(
            trackLeft, bar.Min.Y + (bar.Size.Y - trackHeight) * 0.5f));
        var image = _image;
        Slider(
            _opacityId,
            image.Entry.Opacity,
            ReferenceImageConfiguration.MinimumOpacity,
            1f,
            next => _session.SetOpacity(image, next),
            new ControlStyle { Width = UiWidth.Fixed(OpacityTrackWidth) },
            help: "How much of the picture shows. It never goes fully "
                + "transparent — a window you cannot see is one you cannot "
                + "close.");

        TextInBand(
            new Vector2(trackRight - readout, bar.Min.Y),
            new Vector2(readout, bar.Size.Y),
            (image.Entry.Opacity * 100f).ToString(
                "0", CultureInfo.InvariantCulture) + "%",
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Family = FontFamily.Mono,
                Color = theme.TextDim,
            },
            TextAlign.End);
    }
}
