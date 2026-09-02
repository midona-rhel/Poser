using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Bindings;
using Poser.Game.Overlays;
using Poser.Game.Scene;

namespace Poser.UI;

/// <summary>
/// The selected overlay node's editor — the pane behind the "Overlay" tab
/// that stands while an OVERLAYS sidebar row is selected.
///
/// <para>An overlay node is the one scene entity with no WORLD transform: it
/// lives in screen space, so its placement is two pixel numbers, one uniform
/// scale and an opacity rather than a gizmo, and those rows live HERE rather
/// than on the inspector rail every other entity's transform uses. Ktisis
/// makes the same split (<c>Interface/Editor/Properties/OverlayPropertyList.cs:82-119</c>).
/// </para>
///
/// <para>The rest of the pane is the node's own vocabulary, which is a
/// function of its kind: the dialogue panel's speaker and plate, the balloon's
/// channel and tail, the status line's kind and icon.</para>
///
/// <para>Lifetime clicks are DEFERRED to the end of the frame: destroying the
/// node republishes the scene mid-walk otherwise — the props pane's rule.</para>
/// </summary>
public sealed class OverlayPane
{
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;
    private readonly StatusIconCatalog _statusIcons;

    /// <summary>Adding and removing a node goes through the lifecycle seam, so
    /// both land in the shell's undo history.</summary>
    private readonly SceneLifecycleHistory _lifecycle;

    private readonly GameIconResolver _icons;

    /// <summary>The status sheet's icons, flat and searchable. The rows are a
    /// snapshot minted at open, not per frame.</summary>
    private readonly Crystarium.SearchPicker<StatusIconChoice> _iconPicker =
        new("overlay-status-icon");

    private readonly List<StatusIconChoice> _iconChoices = new();

    private bool _openPlacement = true;

    /// <summary>Whether remove-all confirmation is armed — the camera
    /// pane's destroy-all idiom: a whole-set destroyer takes two presses.
    /// </summary>
    private bool _removeAllArmed;
    private bool _openContent = true;
    private bool _openActions = true;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;

    /// <summary>The node a create or duplicate made, selected once the scene
    /// refresh has bound it.</summary>
    private readonly global::Poser.UI.Composition.PendingSelection<OverlayNodeHandle> _pendingSelect = new();

    private string _status = string.Empty;

    /// <summary>One pickable icon, as a picker row. The picker takes reference
    /// types and the catalog entry is a struct, so the label and the ImGui key
    /// are minted at open rather than per frame.</summary>
    private sealed record StatusIconChoice(
        uint IconId, string Name, string Key);

    public OverlayPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        StatusIconCatalog statusIcons,
        SceneLifecycleHistory lifecycle,
        ITextureProvider textures,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names)
    {
        _scene = scene;
        _bindings = bindings;
        _statusIcons = statusIcons;
        _lifecycle = lifecycle;
        _icons = new GameIconResolver(textures);
        _scenePane = scenePane;
        _names = names;
    }

    private readonly ScenePane _scenePane;
    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    /// <summary>Selects a node some other surface just created — the spawn
    /// browser's rows and this pane's own duplicate. The scene has not
    /// rescanned yet, so the id is resolved on a later frame.</summary>
    public void SelectWhenBound(OverlayNodeHandle? node)
    {
        if (node != null)
            _pendingSelect.Arm(node);
    }

    /// <summary>The shell's every-frame pump. A node created from the spawn
    /// browser has nothing selected yet, so this pane is not being drawn when
    /// the scene refresh binds it — the pending select would never land if it
    /// were only reconciled from <see cref="Draw"/>. The camera pane's rule.
    /// </summary>
    public void Tick() => ReconcilePendingSelect();

    public void Draw(Vector2 origin, Vector2 size)
    {
        ReconcilePendingSelect();

        Crystarium.Page("overlay", origin, size, page =>
        {
            if (SelectedNode() is not { } node)
            {
                page.EmptyState("Select an overlay in the sidebar.");
                return;
            }

            page.Section(
                "Placement",
                _openPlacement,
                next => _openPlacement = next,
                form => PlacementRows(form, node),
                // The house rule every other pane states: a divider stands
                // BETWEEN sections, so the page's first draws neither the rule
                // nor the margin above it.
                divider: false);
            page.Section(
                ContentTitle(node.Kind),
                _openContent,
                next => _openContent = next,
                form => ContentRows(form, node));
            page.Section(
                "Lifetime",
                _openActions,
                next => _openActions = next,
                form => LifetimeRows(form, node),
                divider: false);
        });

        // Pumped after the page: the surface a row opened has to outlive that
        // row's own draw call.
        if (_iconPicker.Draw() is { } picked)
            ApplyIcon(picked.Item);

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void PlacementRows(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        string name = node.Name;
        form.TextInput(
            "Name",
            name,
            next => node.Name = next,
            placeholder: "Overlay",
            help: "What the sidebar calls this overlay — never the text it "
                + "draws");
        // Short rows pair two-up (the standard): the switches share a
        // line, and so do the two scale-ish sliders.
        form.Pair(
            "Visible",
            cell => cell.Switch(
                "##overlay-visible",
                node.Visible,
                next => node.Visible = next,
                help: "Hide the overlay without destroying it"),
            "Drag on screen",
            cell => cell.Switch(
                "##overlay-draggable",
                node.Draggable,
                next => node.Draggable = next,
                help: "Grab the overlay itself and drag it"));
        ScreenPointRows(form, node);
        form.Pair(
            "Scale",
            cell => cell.Slider(
                "##overlay-scale",
                node.Scale,
                OverlayNodeLimits.MinScale,
                OverlayNodeLimits.MaxScale,
                next => node.Scale = next,
                help: "Draw the overlay larger or smaller"),
            "Opacity",
            cell => cell.Slider(
                "##overlay-opacity",
                node.Alpha,
                0f,
                1f,
                next => node.Alpha = next,
                help: "Fade the whole overlay"));

        form.Actions("Position", actions =>
        {
            actions.Button(
                "Centre",
                () => node.Position = Centred(node),
                help: "Move the overlay to the middle of the viewport");
            actions.Button(
                "Reset size",
                () =>
                {
                    node.Scale = 1f;
                    node.Alpha = 1f;
                },
                help: "Back to full size and full opacity");
        });
    }

    // ── the rail's overlay arm ───────────────────────────────────────────

    /// <summary>Whether the inspector rail has an overlay node to edit. The
    /// camera pane's <c>HasRailCamera</c>, for the same reason: the rail asks
    /// the pane that owns the entity rather than resolving it a second
    /// time.</summary>
    public bool HasRailNode => SelectedNode() != null;

    /// <summary>The rail pad's node — the camera pane's BallCamera idiom:
    /// the rail asks the pane that owns the entity.</summary>
    public OverlayNodeHandle? RailNode => SelectedNode();

    /// <summary>
    /// The rail's section for an overlay node — the three facts a node is
    /// adjusted BY while the eye is on the shot: where it sits, what it says,
    /// and whether it can be dragged there directly.
    ///
    /// <para>It is the same seam as the pane, literally: both call the row
    /// helpers below, so a well edited here and a well edited on the Overlay
    /// tab are one control drawn twice and can never disagree. The rail does
    /// NOT carry scale, opacity or the kind's own vocabulary — those are the
    /// tab's, and duplicating a whole pane onto the rail is what the rail is
    /// not for.</para>
    ///
    /// <para>An overlay's placement is SCREEN pixels, so this stands in place
    /// of the world TRANSLATION section every other primary declares — a world
    /// gizmo has nothing to say about a node that lives in the viewport's own
    /// coordinates.</para>
    /// </summary>
    public void DrawRailPlacement(Crystarium.FormScope form)
    {
        if (SelectedNode() is not { } node)
            return;
        ScreenPointRows(form, node);
        TextRow(form, node);
        DraggableRow(form, node);
    }

    /// <summary>The X and Y wells. Both surfaces draw these, so the pixel
    /// format, the per-pixel rate and the wheel step are stated once.</summary>
    private static void ScreenPointRows(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        var position = node.Position;
        form.Cells(cells =>
        {
            cells.Cell(
                "X",
                cell => cell.Number(
                    "##overlay-x",
                    position.X,
                    next => node.Position = new Vector2(next, position.Y),
                    perPixel: 1f,
                    format: "0"));
            cells.Cell(
                "Y",
                cell => cell.Number(
                    "##overlay-y",
                    position.Y,
                    next => node.Position = new Vector2(position.X, next),
                    perPixel: 1f,
                    format: "0"));
        },
        help: "Where the overlay sits, in screen pixels from the top-left");
    }

    private static void DraggableRow(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.Switch(
            "Drag on screen",
            node.Draggable,
            next => node.Draggable = next,
            help: "Grab the overlay itself and drag it");
    }

    /// <summary>The node's own words. The LABEL is the kind's, because "Line"
    /// and "Effect" are what the tab calls the same field — a rail row that
    /// renamed it would read as a second, different setting.</summary>
    private static void TextRow(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        bool status = node.Kind is not (
            OverlayNodeKind.Talk or OverlayNodeKind.Balloon);
        form.TextInput(
            status ? "Effect" : "Line",
            node.Text,
            next => node.Text = next,
            placeholder: status ? "What the effect is called" : "What they say",
            help: status
                ? "The name the status bar shows"
                : "The words this overlay draws");
    }

    private void ContentRows(Crystarium.FormScope form, OverlayNodeHandle node)
    {
        switch (node.Kind)
        {
            case OverlayNodeKind.Talk:
                TalkRows(form, node);
                return;
            case OverlayNodeKind.Balloon:
                BalloonRows(form, node);
                return;
            default:
                StatusRows(form, node);
                return;
        }
    }

    private static void TalkRows(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.TextInput(
            "Speaker",
            node.Speaker,
            next => node.Speaker = next,
            placeholder: "Who is talking",
            help: "The name on the plate above the panel");
        form.TextInput(
            "Line",
            node.Text,
            next => node.Text = next,
            placeholder: "What they say",
            help: "The panel's body, up to "
                + OverlayNodeLimits.MaxTextCharacters + " characters");
        form.Pair(
            "Panel",
            cell => cell.Dropdown(
                "##talk-panel",
                TalkBackgroundLabels,
                (int)node.TalkBackground,
                next => node.TalkBackground = (TalkBackground)next,
                help: "Which dialogue plate to draw on"),
            "Advance mark",
            cell => cell.Dropdown(
                "##talk-cursor",
                TalkCursorLabels,
                (int)node.TalkCursor,
                next => node.TalkCursor = (TalkCursor)next,
                help: "The mark in the panel's corner"));
        FontSizeRow(form, node);
    }

    private static void BalloonRows(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.TextInput(
            "Line",
            node.Text,
            next => node.Text = next,
            placeholder: "What they say",
            help: "The bubble holds one line; longer text is cut with an "
                + "ellipsis, exactly as the game's own bubbles are");
        form.Pair(
            "Channel",
            cell => cell.Dropdown(
                "##balloon-channel",
                BalloonChannelLabels,
                (int)node.BalloonChannel,
                next => node.BalloonChannel = (BalloonChannel)next,
                help: "Which chat channel's frame to wear"),
            "Tint",
            cell => cell.Dropdown(
                "##balloon-tint",
                BalloonGradientLabels,
                (int)node.BalloonGradient,
                next => node.BalloonGradient = (BalloonGradient)next,
                help: "The colour over the gradient band"));
        form.Pair(
            "Tail",
            cell => cell.Switch(
                "##balloon-tail",
                node.ArrowVisible,
                next => node.ArrowVisible = next,
                help: "The point that marks who is speaking"),
            "Tail position",
            cell => cell.Slider(
                "##balloon-tail-position",
                node.ArrowX,
                OverlayNodeLimits.MinArrowX,
                OverlayNodeLimits.MaxArrowX,
                next => node.ArrowX = next,
                format: "0",
                disabled: !node.ArrowVisible,
                help: "Where along the bottom edge the tail sits"));
        FontSizeRow(form, node);
    }

    private void StatusRows(Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.TextInput(
            "Effect",
            node.Text,
            next => node.Text = next,
            placeholder: "What the effect is called",
            help: "The name the status bar shows");
        string current = _statusIcons.NameFor(node.StatusIconId);
        form.Pair(
            "Reads as",
            cell => cell.Dropdown(
                "##status-kind",
                StatusKindLabels,
                (int)node.StatusKind,
                next => node.StatusKind = (StatusKind)next,
                help: "Gained reads as an addition, expiring as a "
                    + "subtraction"),
            "Icon",
            cell => cell.Picker(
                "##status-icon-pick",
                current.Length > 0
                    ? current
                    : node.StatusIconId == 0
                        ? "None"
                        : "Icon " + node.StatusIconId,
                () => OpenIconPicker(node),
                help: "Any status icon the game declares"));
    }

    private static void FontSizeRow(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.NumericSlider(
            "Text size",
            node.FontSize,
            OverlayNodeLimits.MinFontSize,
            OverlayNodeLimits.MaxFontSize,
            next => node.FontSize = (uint)MathF.Round(next),
            perPixel: 0.2f,
            format: "0",
            help: "Point size of the drawn text");
    }

    private void LifetimeRows(
        Crystarium.FormScope form, OverlayNodeHandle node)
    {
        form.Actions("Library", actions =>
            actions.Button(
                "Save to library",
                () => _names.Open(
                    "Save overlay to library", node.Name,
                    name =>
                    {
                        if (_bindings.GetOverlayId(node) is { } entryId)
                            _scenePane.SaveOverlayEntry(
                                entryId.LogicalId, name);
                    }),
                help: "Save this overlay as a library entry"));
        form.Actions("Overlay", actions =>
        {
            actions.Button(
                "Duplicate",
                () => _pending = () => Duplicate(node),
                help: "Add another overlay saying exactly this");
            actions.Button(
                "Delete",
                () => _pending = () =>
                {
                    _lifecycle.DestroyOverlay(node);
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Take this overlay off the screen");
            actions.Button(
                _removeAllArmed ? "Confirm remove all" : "Remove all",
                () => _pending = () =>
                {
                    if (!_removeAllArmed)
                    {
                        _removeAllArmed = true;
                        return;
                    }
                    _removeAllArmed = false;
                    _lifecycle.DestroyAllOverlays();
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Take every overlay off the screen");
        });
        form.Status(
            _status.Length > 0
                ? _status
                : "Overlays are drawn behind the game's own interface and "
                    + "survive hiding the UI, so they stay in the shot.");
    }

    // ── the icon picker ──────────────────────────────────────────────────

    private void OpenIconPicker(OverlayNodeHandle node)
    {
        _iconChoices.Clear();
        foreach (var entry in _statusIcons.Entries)
            _iconChoices.Add(new StatusIconChoice(
                entry.IconId,
                entry.Name,
                entry.IconId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));

        _iconPicker.Open(
            "status-icon",
            _iconChoices,
            static choice => choice.Name,
            static choice => choice.Key,
            node.StatusIconId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            loadError: _iconChoices.Count == 0
                ? "The status sheet declared no icons."
                : null,
            options: new PickerOptions<StatusIconChoice>
            {
                // A picture picker has to show the pictures: the row's mark is
                // the icon itself, not a glyph standing in for one.
                Texture = choice => _icons.Resolve(choice.IconId),
            });
    }

    private void ApplyIcon(StatusIconChoice choice)
    {
        if (SelectedNode() is { } node)
            node.StatusIconId = choice.IconId;
    }

    // ── acts ─────────────────────────────────────────────────────────────

    /// <summary>Public because the overlay row's context menu speaks this
    /// verb too — one duplication rule, wherever it is asked.</summary>
    public OverlayNodeHandle? Duplicate(OverlayNodeHandle node)
    {
        // The copy is offset so it does not land exactly under the original,
        // where it would look like nothing happened; the NAME is dropped so
        // the service mints the next one of its kind rather than two rows
        // wearing one name.
        var document = node.State with
        {
            Name = string.Empty,
            Position = node.Position + new Vector2(DuplicateOffset),
        };
        if (_lifecycle.SpawnOverlay(document) is OverlayNodeHandle copy)
        {
            _pendingSelect.Arm(copy);
            _status = string.Empty;
            return copy;
        }
        _status = "The overlay could not be duplicated — the game's interface "
            + "would not take it.";
        return null;
    }

    /// <summary>How far a duplicate lands from its original, in the node's own
    /// screen pixels.</summary>
    private const float DuplicateOffset = 24f;

    private static Vector2 Centred(OverlayNodeHandle node)
    {
        var viewport = ImGui.GetMainViewport().Size;
        // The same extent the node layer gives the game as the node's own size,
        // so the middle a node is centred on is the middle you can grab it by.
        var extent = OverlayNodeGeometry.DesignSize(node.Kind) * node.Scale;
        return (viewport - extent) * 0.5f;
    }

    // ── state ────────────────────────────────────────────────────────────

    private OverlayNodeHandle? SelectedNode()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId })
            return null;
        var resolved = _bindings.Resolve(overlayId);
        return resolved.Success && resolved.Value is { IsValid: true } node
            ? node
            : null;
    }

    /// <summary>Second half of <see cref="SelectWhenBound"/>: once the scene
    /// refresh has bound the new node, select it and forget it.</summary>
    private void ReconcilePendingSelect()
    {
        _pendingSelect.Reconcile(
            node => _bindings.GetOverlayId(node) is { } id
                ? SelectionId.ForOverlay(id)
                : null,
            _scene.Selection,
            stillValid: node => node.IsValid);
    }

    private static string ContentTitle(OverlayNodeKind kind) => kind switch
    {
        // Sentence case, the header rule.
        OverlayNodeKind.Balloon => "Bubble",
        OverlayNodeKind.Status => "Status",
        _ => "Dialogue",
    };

    // The label sets are positional against their enums, minted once: a
    // dropdown that rebuilt its list per frame would be this pane's whole
    // warm-frame cost.

    private static readonly string[] TalkBackgroundLabels =
    [
        "Basic",
        "Thought",
        "Echo",
        "Computer",
        "Yell",
        "Parchment",
        "Dragonspeak",
        "Linkpearl",
        "Narration",
    ];

    private static readonly string[] TalkCursorLabels =
    [
        "None",
        "Page turn",
        "Continue",
    ];

    private static readonly string[] BalloonChannelLabels =
    [
        "Say",
        "Party",
        "Tell",
        "Alliance",
        "Yell",
        "Shout",
        "Free Company",
        "Linkshell",
        "Cross-world linkshell",
        "Novice Network",
        "PvP team",
    ];

    private static readonly string[] BalloonGradientLabels =
    [
        "Default",
        "Lime",
        "Orange",
        "Violet",
        "Sky blue",
        "Clay",
        "Light jeans",
        "Grass green",
        "Grey",
        "Pink",
        "Dark jeans",
        "Green",
        "Purple",
        "Brown",
        "Cloudy blue",
        "Royal purple",
    ];

    private static readonly string[] StatusKindLabels =
    [
        "Plain",
        "Gained",
        "Suffered",
        "Expiring",
    ];
}
