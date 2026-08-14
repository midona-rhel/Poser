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
/// <para>THE WINDOW IS THE PICTURE. Unhovered it carries nothing but the
/// image, wearing the same rounded corners and the same glass edge every
/// Poser window wears — <see cref="Crystarium.FloatingSurface.DrawChrome"/>
/// with its fill and blur off, because a fill under a picture the user can
/// make translucent would be a second image. Hovered, the standard title bar
/// FADES IN over the top of the picture — the same
/// <see cref="Crystarium.WindowFrame"/> bar every floating surface wears,
/// carrying the picture's name, its opacity control and the close action. The
/// bar overlays; it never takes layout, so the aspect ratio is the whole
/// window's and the picture is never letterboxed.</para>
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

    public ReferenceImageWindow(
        ReferenceImageSession session, ReferenceImageInstance image)
        : base($"###poser_reference_image_{image.Id}",
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

    public override void PreDraw()
    {
        base.PreDraw();

        var entry = _image.Entry;
        float aspect = _image.Aspect;

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
                float gs = MathF.Max(ImGuiHelpers.GlobalScale, 0.01f);
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
            // grip keeps its own authority.
            if (MathF.Abs(target.X - _observed.X) > 0.5f
                || MathF.Abs(target.Y - _observed.Y) > 0.5f)
            {
                Size = target;
                SizeCondition = ImGuiCond.Always;
            }
            else
            {
                SizeCondition = ImGuiCond.FirstUseEver;
            }
        }

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(ReferenceImageGeometry.MinimumSide),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding,
            Crystarium.ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
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
            bool hasImage = _image.Handle != 0;
            // Edge ONLY while a picture is up: the glass fill and its blur
            // would sit under an image the user can make translucent, and two
            // grounds read as neither. With no picture the empty state needs
            // the full chassis to stand on.
            Crystarium.FloatingSurface.DrawChrome(
                draw, min, max, theme.Radii.Window,
                shadow: true,
                blur: !hasImage,
                fill: !hasImage);

            if (hasImage)
                draw.AddImageRounded(
                    new ImTextureID(_image.Handle),
                    min,
                    max,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(
                            new Vector4(1f, 1f, 1f, _image.Entry.Opacity))),
                    theme.Radii.Window * scale);
            else
                DrawEmptyState(min, size);

            DrawTitleBar(min, size, scale, draw);
        }
        finally
        {
            Interactive.EndOwner(owner);
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
