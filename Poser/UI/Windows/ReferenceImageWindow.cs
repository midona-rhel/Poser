using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Config;

namespace Poser.UI;

/// <summary>
/// One reference picture, floating over the game.
///
/// <para>THE WINDOW IS THE PICTURE, corner to corner. The image is drawn from
/// <c>GetWindowPos()</c> to <c>GetWindowPos() + GetWindowSize()</c> with the
/// window's padding and border pushed to zero, so there is NO inset between
/// the chrome edge and the picture — which is also what lets
/// <see cref="ReferenceImageGeometry.ResolveAspect"/> clamp the WINDOW's size
/// to the PICTURE's ratio directly: with zero insets the two rectangles are
/// the same rectangle, and no inset has to be subtracted before the ratio is
/// taken or added back after.</para>
///
/// <para>THE OPACITY GOVERNS THE WHOLE WINDOW, because seeing through the
/// picture is the point. Under a picture there is no ground of any kind: no
/// glass fill, no backdrop blur, and NO ELEVATION SHADOW — a blurred shadow
/// is a solid rect behind the box plus a feather reaching a dozen logical
/// pixels past it, which is both the mat behind the picture and the margin
/// around it. Only the 1px glass edge survives, and it is drawn under the
/// picture's own alpha so a picture at the floor wears a floor-alpha edge.
/// Hovered, the standard title bar FADES IN over the top — the same
/// <see cref="Crystarium.WindowFrame"/> bar every floating surface wears,
/// carrying the picture's name, its opacity control and the close action. The
/// bar overlays; it never takes layout, so the aspect ratio is the whole
/// window's and the picture is never letterboxed. It keeps its own opacity:
/// the bar is only up while the user is working it, and a control they cannot
/// read is not a control.</para>
///
/// <para>With no picture up there is nothing to see through, and the empty
/// state stands on the full chassis — fill, blur, shadow and edge.</para>
///
/// <para>The fade is the overlay-tooltip idiom without the pop: a
/// constant-rate ramp eased on <see cref="Transition.PictoDefault"/>, applied
/// as <see cref="ImGuiStyleVar.Alpha"/> so the whole bar — scrim, rule, label,
/// slider, close — fades as ONE surface through the shared
/// <c>ColorEx.ApplyAlpha</c> path. No scale, no rise.</para>
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

    /// <summary>The last size this window actually wore, logical — the
    /// reference <see cref="ReferenceImageGeometry.ResolveAspect"/> measures
    /// the user's drift against, and therefore always a CONFORMANT size.
    /// </summary>
    private Vector2 _applied;

    /// <summary>What ImGui reported last frame, logical. PreDraw runs before
    /// Draw, so the correction it applies is always against the previous
    /// frame's observation.</summary>
    private Vector2 _observed;

    /// <summary>0..1 raw ramp behind the title bar's fade.</summary>
    private float _barRamp;

    /// <summary>A real seat has been applied — a stored placement, or the
    /// picture's own pixels. Until then the window wears a placeholder box and
    /// writes NOTHING back, or the placeholder would become the stored
    /// placement on the next frame.</summary>
    private bool _seated;

    /// <summary>The one-frame <see cref="Window.Position"/> write is still
    /// armed. Dalamud re-applies a non-null Position every frame, so an
    /// uncleared Always seat is a window that can never be dragged.</summary>
    private bool _positionApplied;

    private bool _hovered;

    /// <summary>The ratio <see cref="ConformToAspect"/> is to hold, handed over
    /// for the length of one <c>Begin</c>. Static because the callback is a
    /// plain function with no state of its own, and only ever runs between the
    /// <c>SetNextWindowSizeConstraints</c> that arms it and the <c>Begin</c>
    /// immediately after — one window, one thread, no overlap.</summary>
    private static float _constraintAspect;

    /// <summary>
    /// The aspect lock, applied WHILE the resize is being resolved rather than
    /// after it. The dominant drag axis stays authoritative — whichever edge
    /// the hand actually moved is the one kept, and the other is derived from
    /// it — so a corner drag does not fight itself and an edge drag reads as
    /// the picture growing rather than as a box being squared up afterwards.
    /// </summary>
    private static unsafe void ConformToAspect(ImGuiSizeCallbackData* data)
    {
        float aspect = _constraintAspect;
        if (aspect <= 0f || data == null)
            return;
        var current = data->CurrentSize;
        var desired = data->DesiredSize;
        data->DesiredSize =
            MathF.Abs(desired.X - current.X) >= MathF.Abs(desired.Y - current.Y)
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
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
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
            Crystarium.ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
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

        var theme = Crystarium.ActiveTheme;
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
                Crystarium.FloatingSurface.DrawChrome(
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
    /// The picture, edge to edge, and its edge — in that order and nothing
    /// else. Both wear the SAME alpha: the image multiplies its tint by the
    /// stored opacity, and the edge is drawn under a style alpha scaled by the
    /// same number, which is the one path <c>ColorEx.ApplyAlpha</c> already
    /// carries every chrome colour through. Anything that would ground the
    /// picture — the glass fill, the backdrop blur, the elevation shadow — is
    /// off, so at the opacity floor the world behind shows through the whole
    /// window and not through a mat.
    /// </summary>
    private void DrawPicture(
        ImDrawListPtr draw, Vector2 min, Vector2 max, float scale)
    {
        var theme = Crystarium.ActiveTheme;
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
            Crystarium.FloatingSurface.DrawChrome(
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
        Crystarium.TextInBand(
            min,
            size,
            _image.Failure ?? "Reading the picture…",
            new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                Color = Crystarium.ActiveTheme.FormHint,
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
                    / Transition.PictoDefault.DurationSeconds,
            0f,
            1f);
        float fade = Transition.PictoDefault.Evaluate(_barRamp);
        // Submitted only while it is visible: a bar at zero alpha still
        // reserves its close button, and an invisible close is a trap.
        if (fade <= 0.001f)
            return;

        var theme = Crystarium.ActiveTheme;
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
                        Crystarium.FloatingSurface.FillColor)),
                theme.Radii.Window * scale,
                ImDrawFlags.RoundCornersTop);

            Crystarium.WindowFrame(
                _frameId,
                min,
                size,
                new WindowFrameProps
                {
                    Title = _image.Name,
                    TitleContent = _titleContent,
                    OnClose = _close,
                    CloseHelp = "Close this reference image",
                    // The window painted its own chassis above; the frame must
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
        var theme = Crystarium.ActiveTheme;
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
            Crystarium.TextInBand(
                new Vector2(bar.Min.X + inset, bar.Min.Y),
                new Vector2(labelWidth, bar.Size.Y),
                _image.Name,
                labelStyle,
                Crystarium.MeasureText(_image.Name, labelStyle).X > labelWidth
                    ? TextConstraint.Truncate(labelWidth)
                    : TextConstraint.Intrinsic);

        float trackHeight = theme.Controls.SliderHeight * scale;
        ImGui.SetCursorScreenPos(new Vector2(
            trackLeft, bar.Min.Y + (bar.Size.Y - trackHeight) * 0.5f));
        var image = _image;
        Crystarium.Slider(
            _opacityId,
            image.Entry.Opacity,
            ReferenceImageConfiguration.MinimumOpacity,
            1f,
            next => _session.SetOpacity(image, next),
            new ControlStyle { Width = UiWidth.Fixed(OpacityTrackWidth) },
            help: "How much of the picture shows. It never goes fully "
                + "transparent — a window you cannot see is one you cannot "
                + "close.");

        Crystarium.TextInBand(
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
