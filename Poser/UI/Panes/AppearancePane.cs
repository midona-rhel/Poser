using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Presentation;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The Appearance tab: a compact actor-scoped form for the runtime
/// effects Poser owns — opacity, whole-model tints, and the granular
/// wet-surface override — plus the one outbound Open-in-Glamourer
/// action. Everything else about appearance belongs to Glamourer.
///
/// Draws into the shell's scroll (no rail, no own viewport) on the
/// shared inspector form geometry; rows keep their place when a weapon
/// model is absent — the row shows unavailable rather than vanishing
/// and shifting the form.
/// </summary>
public sealed class AppearancePane
{
    private readonly ActorPresentationSession _presentation;
    private readonly ActorIntegrationSession _integration;
    private readonly SceneSession _scene;

    private string _status = string.Empty;
    private const float ContentPadding = 12f;

    /// <summary>The ONE stable-id display lookup every surface uses
    /// (nickname, else anonymous mask, else the cleaned snapshot name) --
    /// wired by the window so this pane shows exactly what the sidebar
    /// and crumb show.</summary>
    public Func<ActorDescriptor, string>? DisplayNameProvider;

    public AppearancePane(
        ActorPresentationSession presentation,
        ActorIntegrationSession integration,
        SceneSession scene)
    {
        _presentation = presentation;
        _integration = integration;
        _scene = scene;
    }

    /// <summary>The actor the tab acts on: the selected actor, or the
    /// owning actor of a selected bone. Selection itself is untouched.</summary>
    private ActorId? TargetActor() => _scene.Selection.Primary switch
    {
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
        _ => null,
    };

    private ActorDescriptor? Describe(ActorId id)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.Equals(id))
                return actor;
        return null;
    }

    public void Draw(Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        float width = InspectorLayout.ClampContentWidth(size.X, s);

        if (TargetActor() is not { } actor)
        {
            InspectorLayout.EmptyState(origin, s);
            return;
        }
        if (!_presentation.IsSupported(actor) || _presentation.Read(actor) is not { } reading)
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * s,
                "This actor does not support appearance effects.", 12f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return;
        }

        var owned = _presentation.OverridesFor(actor);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, ContentPadding * s));
        var cursor = ImGui.GetCursorScreenPos();
        float y = 0f;
        float controlX = InspectorLayout.FormControlX(cursor.X, s);
        float controlW = InspectorLayout.FormControlWidth(width, s);

        void Report(PresentationResult result, string what) =>
            _status = result.Success ? string.Empty : $"{what}: {result.Detail}";

        void RowHelp(float top, string id, string help)
        {
            var helpMin = new Vector2(cursor.X, top);
            var helpMax = new Vector2(cursor.X + width, top + InspectorLayout.FormRowHeight * s);
            if (Crystarium.HoverHelp.HelpHovered(helpMin, helpMax))
                Crystarium.HoverHelp.Explain(id, helpMin, helpMax, help);
        }

        float Caption(string text)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + y), text, 11f,
                FontWeight.SemiBold, InspectorLayout.LabelColor);
            return 20f * s;
        }

        float SliderRow(string id, string label, float value, float min, float max,
            string fmt, string help, bool disabled, Action<float> apply)
        {
            float rowTop = cursor.Y + y;
            InspectorLayout.FormLabel(new Vector2(cursor.X, rowTop), label, s);
            ImGui.SetCursorScreenPos(new Vector2(
                controlX, rowTop + InspectorLayout.FormSliderY * s));
            float edit = value;
            if (Crystarium.Slider(id, ref edit, min, max, new SliderProps
                {
                    Disabled = disabled,
                    Style = new SliderStyle
                    {
                        Width = Sizing.Fixed(controlW - InspectorLayout.FormValueColumnWidth),
                    },
                }) && !disabled)
                apply(edit);
            string readout = string.Format(fmt, edit);
            ViewText.Label(new Vector2(
                    cursor.X + width - ViewText.Measure(readout, 11f, mono: true),
                    rowTop + InspectorLayout.FormLabelY * s),
                readout, 11f, FontWeight.Regular, InspectorLayout.LabelColor, mono: true);
            RowHelp(rowTop, id + "-row", help);
            return InspectorLayout.FormRowHeight * s;
        }

        float TintRow(string id, string label, PresentationModel model, string help)
        {
            float rowTop = cursor.Y + y;
            InspectorLayout.FormLabel(new Vector2(cursor.X, rowTop), label, s);
            Vector4? current = owned.Tints.TryGetValue(model, out var ownedTint)
                ? ownedTint
                : reading.TintFor(model);
            if (current is { } tint)
            {
                // 28px well centred in the 30px form row.
                ImGui.SetCursorScreenPos(new Vector2(controlX, rowTop + 1f * s));
                var edit = tint;
                // RGB only: the tint's alpha channel is the model's own
                // and is preserved exactly.
                if (Crystarium.ColorWell(id, ref edit, rgbOnly: true))
                    Report(_presentation.SetTint(actor, model, edit), label);
            }
            else
            {
                // The absent model keeps its row: nothing shifts, nothing
                // is redirected to another model.
                ViewText.Label(new Vector2(controlX, rowTop + InspectorLayout.FormLabelY * s),
                    "Not present", 11f, FontWeight.Regular, InspectorLayout.HintColor);
            }
            RowHelp(rowTop, id + "-row", help);
            return InspectorLayout.FormRowHeight * s;
        }

        // ── Header: actor name, Open in Glamourer, Reset appearance ───
        float headerTop = cursor.Y + y;
        var descriptor = Describe(actor);
        string headerName = descriptor is { } described
            ? DisplayNameProvider?.Invoke(described) ?? described.Name
            : "Actor";
        ViewText.Label(new Vector2(cursor.X, headerTop + InspectorLayout.FormLabelY * s),
            headerName, 11f, FontWeight.SemiBold, InspectorLayout.LabelColor);
        var glamourer = _integration.Glamourer;
        bool glamAvailable = glamourer.Available;
        string glamReason = glamAvailable ? "Open this actor in Glamourer." : glamourer.Detail;
        float bx = cursor.X + width;
        var resetSize = Crystarium.MeasureButton("Reset appearance", Cls.Compact);
        bx -= resetSize.X;
        ImGui.SetCursorScreenPos(new Vector2(bx, headerTop + InspectorLayout.FormButtonY * s));
        if (Crystarium.Button("Reset appearance", new ButtonProps
            {
                Id = "app-reset",
                Classes = Cls.Compact,
                Tooltip = "Restore this actor's incoming opacity, tints, and wetness",
            }))
            Report(_presentation.ResetActor(actor), "Reset appearance");
        var glamSize = Crystarium.MeasureButton("Open in Glamourer", Cls.Compact);
        bx -= 8f * s + glamSize.X;
        ImGui.SetCursorScreenPos(new Vector2(bx, headerTop + InspectorLayout.FormButtonY * s));
        if (Crystarium.Button("Open in Glamourer", new ButtonProps
            {
                Id = "app-glamourer",
                Classes = Cls.Compact,
                Disabled = !glamAvailable,
                // The availability reason doubles as the help for the
                // disabled action; when available it explains behavior.
                Tooltip = glamReason,
            }))
        {
            var opened = _integration.OpenGlamourer(actor);
            _status = opened.Success ? string.Empty : $"Open in Glamourer: {opened.Detail}";
        }
        y += InspectorLayout.FormRowHeight * s;

        if (_status.Length > 0)
        {
            ViewText.Label(new Vector2(cursor.X, cursor.Y + y + 2f * s), _status, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            y += 20f * s;
        }
        y += 10f * s;

        // ── Presentation ──────────────────────────────────────────────
        y += Caption("PRESENTATION");
        float opacity = owned.Opacity ?? reading.Opacity;
        y += SliderRow("##app-opacity", "Opacity", opacity, 0f, 1f, "{0:0.00}",
            "Fade the whole actor; 0 is fully invisible and never touches the visibility action",
            disabled: false,
            value => Report(_presentation.SetOpacity(actor, value), "Opacity"));
        y += TintRow("##app-tint-character", "Character", PresentationModel.Character,
            "Multiply the character model's colors");
        y += TintRow("##app-tint-main", "Main hand", PresentationModel.MainHand,
            "Multiply the main-hand model's colors");
        y += TintRow("##app-tint-off", "Off hand", PresentationModel.OffHand,
            "Multiply the off-hand model's colors");
        y += 10f * s;

        // ── Wet surface ───────────────────────────────────────────────
        y += Caption("WET SURFACE");
        float overrideTop = cursor.Y + y;
        InspectorLayout.FormLabel(new Vector2(cursor.X, overrideTop), "Override", s);
        bool overrideOn = owned.Wetness != null;
        ImGui.SetCursorScreenPos(new Vector2(
            controlX, overrideTop + InspectorLayout.FormSwitchY * s));
        if (Crystarium.Switch("##app-wet-override", ref overrideOn))
            Report(_presentation.SetWetnessEnabled(actor, overrideOn), "Wetness override");
        RowHelp(overrideTop, "app-wet-override-row",
            "Hold the wet-surface values below against the game's own weather and water updates; turning it off restores the incoming values exactly");
        y += InspectorLayout.FormRowHeight * s;

        bool wetOn = _presentation.OverridesFor(actor).Wetness != null;
        var wet = _presentation.OverridesFor(actor).Wetness ?? reading.Wetness;
        y += SliderRow("##app-wet-weather", "Weather", wet.Weather, 0f, 1f, "{0:0.00}",
            "How rain-wet the surface looks, 0 dry to 1 soaked",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Weather = value }), "Weather"));
        y += SliderRow("##app-wet-swimming", "Swimming", wet.Swimming, 0f, 1f, "{0:0.00}",
            "How water-wet the surface looks, 0 dry to 1 soaked",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Swimming = value }), "Swimming"));
        y += SliderRow("##app-wet-depth", "Depth", wet.Depth, 0f, 3f, "{0:0.00}",
            "How high up the body the wetness reaches, in about character heights",
            disabled: !wetOn,
            value => Report(_presentation.SetWetness(actor, wet with { Depth = value }), "Depth"));

        // Register the content extent so the shell's scroll knows the
        // page height (the form fits the retained minimum; this is only
        // the bookkeeping every shell page does).
        ImGui.SetCursorScreenPos(cursor);
        ImGui.Dummy(new Vector2(width, y + ContentPadding * s));
    }
}
