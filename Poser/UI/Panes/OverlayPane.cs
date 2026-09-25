using System;
using Poser.Application.Presentation;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.UI;

/// <summary>
/// The selected overlay node's editor — the pane behind the "Overlay" tab
/// that stands while an OVERLAYS sidebar row is selected.
///
/// <para>Native UI overlays have screen-space placement. Managed IK colliders
/// instead use the shared inspector/gizmo for world transforms; their shape
/// and rendering settings live here.</para>
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
    private readonly IStatusIconCatalog _statusIcons;

    /// <summary>Adding and removing a node goes through the lifecycle seam, so
    /// both land in the shell's undo history.</summary>
    private readonly ISceneCreation _creation;
    private readonly EntityActions _entityActions;

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
    private bool _openContent = true;
    private bool _openActions = true;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;

    /// <summary>The node a create or duplicate made, selected once the scene
    /// refresh has bound it.</summary>
    private readonly global::Poser.UI.Composition.PendingSelection<SceneEntityHandle> _pendingSelect = new();

    private string _status = string.Empty;

    /// <summary>One pickable icon, as a picker row. The picker takes reference
    /// types and the catalog entry is a struct, so the label and the ImGui key
    /// are minted at open rather than per frame.</summary>
    private sealed record StatusIconChoice(
        uint IconId, string Name, string Key);

    public OverlayPane(
        SceneSession scene,
        IStatusIconCatalog statusIcons,
        ISceneCreation creation,
        EntityActions entityActions,
        ITextureProvider textures,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        IOverlayControl values)
    {
        _values = values;
        _scene = scene;
        _statusIcons = statusIcons;
        _creation = creation;
        _entityActions = entityActions;
        _icons = new GameIconResolver(textures);
        _scenePane = scenePane;
        _names = names;
    }

    private readonly ScenePane _scenePane;
    private readonly global::Poser.UI.Controls.EntityNameModal _names;
    private readonly IOverlayControl _values;

    /// <summary>Selects a node some other surface just created — the spawn
    /// browser's rows and this pane's own duplicate. The scene has not
    /// rescanned yet, so the id is resolved on a later frame.</summary>
    private void SelectWhenBound(SceneEntityHandle? node)
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
            if (node.State.Kind != OverlayNodeKind.Collider) page.Section(
                ContentTitle(node.State.Kind),
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
        Crystarium.FormScope form, OverlayReading node)
    {
        if (node.State.Collider is { } collider)
        {
            form.TextInput("Name", node.State.Name, next => _values.SetName(node.Id, next));
            form.Pair("Shape", cell => cell.Dropdown("##collider-shape",
                    collider.Shape == Domain.Posing.IkColliderShape.Mesh
                        ? new[] { "Captured mesh" } : new[] { "Plane", "Box", "Cylinder", "Cone", "Capsule", "Sphere" },
                    collider.Shape == Domain.Posing.IkColliderShape.Mesh ? 0 : (int)collider.Shape > 4 ? (int)collider.Shape - 1 : (int)collider.Shape,
                    next => _values.SetColliderShape(node.Id, (Domain.Posing.IkColliderShape)(next >= 4 ? next + 1 : next)),
                    disabled: collider.Shape == Domain.Posing.IkColliderShape.Mesh),
                "Collision", cell => cell.Switch("##collider-enabled", collider.Enabled,
                    next => _values.SetCollisionEnabled(node.Id, next)));
            form.Pair("Lock transform", cell => cell.Switch("##collider-lock", collider.Locked,
                    next => _values.SetColliderLocked(node.Id, next)),
                "Visible", cell => cell.Switch("##collider-visible", node.State.Visible,
                    next => _values.SetVisible(node.Id, next)));
            form.Slider("Opacity", node.State.Alpha, 0f, 1f, next => _values.SetAlpha(node.Id, next), onBegin: _values.Seal);
            return;
        }
        string name = node.State.Name;
        form.TextInput(
            "Name",
            name,
            next => _values.SetName(node.Id, next),
            placeholder: "Overlay",
            help: "What the sidebar calls this overlay — never the text it "
                + "draws");
        // Short rows pair two-up (the standard): the switches share a
        // line, and so do the two scale-ish sliders.
        form.Pair(
            "Visible",
            cell => cell.Switch(
                "##overlay-visible",
                node.State.Visible,
                next => _values.SetVisible(node.Id, next),
                help: "Hide the overlay without destroying it"),
            "Drag on screen",
            cell => cell.Switch(
                "##overlay-draggable",
                node.State.Draggable,
                next => _values.SetDraggable(node.Id, next),
                help: "Grab the overlay itself and drag it"));
        ScreenPointRows(form, node);
        form.Pair(
            "Scale",
            cell => cell.Slider(
                "##overlay-scale",
                node.State.Scale,
                OverlayNodeLimits.MinScale,
                OverlayNodeLimits.MaxScale,
                next => _values.SetScale(node.Id, next),
                help: "Draw the overlay larger or smaller",
                onBegin: _values.Seal),
            "Opacity",
            cell => cell.Slider(
                "##overlay-opacity",
                node.State.Alpha,
                0f,
                1f,
                next => _values.SetAlpha(node.Id, next),
                help: "Fade the whole overlay",
                onBegin: _values.Seal));

        form.Actions("Position", actions =>
        {
            actions.Button(
                "Centre",
                () => _values.SetPosition(node.Id, Centred(node)),
                help: "Move the overlay to the middle of the viewport");
            actions.Button(
                "Reset size",
                () => _values.ResetSize(node.Id),
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
    public OverlayReading? RailNode => SelectedNode();

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
    private void ScreenPointRows(Crystarium.FormScope form, OverlayReading node)
    {
        var position = node.State.Position;
        form.Cells(cells =>
        {
            cells.Cell(
                "X",
                cell => cell.Number(
                    "##overlay-x",
                    position.X,
                    next => _values.SetPosition(node.Id, new Vector2(next, position.Y)),
                    perPixel: 1f,
                    format: "0",
                    onCommit: null));
            cells.Cell(
                "Y",
                cell => cell.Number(
                    "##overlay-y",
                    position.Y,
                    next => _values.SetPosition(node.Id, new Vector2(position.X, next)),
                    perPixel: 1f,
                    format: "0",
                    onCommit: null));
        },
        help: "Where the overlay sits, in screen pixels from the top-left");
    }

    private void DraggableRow(Crystarium.FormScope form, OverlayReading node)
    {
        form.Switch(
            "Drag on screen",
            node.State.Draggable,
            next => _values.SetDraggable(node.Id, next),
            help: "Grab the overlay itself and drag it");
    }

    /// <summary>The node's own words. The LABEL is the kind's, because "Line"
    /// and "Effect" are what the tab calls the same field — a rail row that
    /// renamed it would read as a second, different setting.</summary>
    private void TextRow(Crystarium.FormScope form, OverlayReading node)
    {
        bool status = node.State.Kind is not (
            OverlayNodeKind.Talk or OverlayNodeKind.Balloon);
        form.TextInput(
            status ? "Effect" : "Line",
            node.State.Text,
            next => _values.SetText(node.Id, next),
            placeholder: status ? "What the effect is called" : "What they say",
            help: status
                ? "The name the status bar shows"
                : "The words this overlay draws");
    }

    private void ContentRows(Crystarium.FormScope form, OverlayReading node)
    {
        switch (node.State.Kind)
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

    private void TalkRows(Crystarium.FormScope form, OverlayReading node)
    {
        form.TextInput(
            "Speaker",
            node.State.Speaker,
            next => _values.SetSpeaker(node.Id, next),
            placeholder: "Who is talking",
            help: "The name on the plate above the panel");
        form.TextInput(
            "Line",
            node.State.Text,
            next => _values.SetText(node.Id, next),
            placeholder: "What they say",
            help: "The panel's body, up to "
                + OverlayNodeLimits.MaxTextCharacters + " characters");
        form.Pair(
            "Panel",
            cell => cell.Dropdown(
                "##talk-panel",
                TalkBackgroundLabels,
                (int)node.State.TalkBackground,
                next => _values.SetTalkBackground(node.Id, (TalkBackground)next),
                help: "Which dialogue plate to draw on"),
            "Advance mark",
            cell => cell.Dropdown(
                "##talk-cursor",
                TalkCursorLabels,
                (int)node.State.TalkCursor,
                next => _values.SetTalkCursor(node.Id, (TalkCursor)next),
                help: "The mark in the panel's corner"));
        FontSizeRow(form, node);
    }

    private void BalloonRows(Crystarium.FormScope form, OverlayReading node)
    {
        form.TextInput(
            "Line",
            node.State.Text,
            next => _values.SetText(node.Id, next),
            placeholder: "What they say",
            help: "The bubble holds one line; longer text is cut with an "
                + "ellipsis, exactly as the game's own bubbles are");
        form.Pair(
            "Channel",
            cell => cell.Dropdown(
                "##balloon-channel",
                BalloonChannelLabels,
                (int)node.State.BalloonChannel,
                next => _values.SetBalloonChannel(node.Id, (BalloonChannel)next),
                help: "Which chat channel's frame to wear"),
            "Tint",
            cell => cell.Dropdown(
                "##balloon-tint",
                BalloonGradientLabels,
                (int)node.State.BalloonGradient,
                next => _values.SetBalloonGradient(node.Id, (BalloonGradient)next),
                help: "The colour over the gradient band"));
        form.Pair(
            "Tail",
            cell => cell.Switch(
                "##balloon-tail",
                node.State.ArrowVisible,
                next => _values.SetArrowVisible(node.Id, next),
                help: "The point that marks who is speaking"),
            "Tail position",
            cell => cell.Slider(
                "##balloon-tail-position",
                node.State.ArrowX,
                OverlayNodeLimits.MinArrowX,
                OverlayNodeLimits.MaxArrowX,
                next => _values.SetArrowX(node.Id, next),
                format: "0",
                disabled: !node.State.ArrowVisible,
                help: "Where along the bottom edge the tail sits"));
        FontSizeRow(form, node);
    }

    private void StatusRows(Crystarium.FormScope form, OverlayReading node)
    {
        form.TextInput(
            "Effect",
            node.State.Text,
            next => _values.SetText(node.Id, next),
            placeholder: "What the effect is called",
            help: "The name the status bar shows");
        string current = _statusIcons.NameFor(node.State.StatusIconId);
        form.Pair(
            "Reads as",
            cell => cell.Dropdown(
                "##status-kind",
                StatusKindLabels,
                (int)node.State.StatusKind,
                next => _values.SetStatusKind(node.Id, (StatusKind)next),
                help: "Gained reads as an addition, expiring as a "
                    + "subtraction"),
            "Icon",
            cell => cell.Picker(
                "##status-icon-pick",
                current.Length > 0
                    ? current
                    : node.State.StatusIconId == 0
                        ? "None"
                        : "Icon " + node.State.StatusIconId,
                () => OpenIconPicker(node),
                help: "Any status icon the game declares"));
    }

    private void FontSizeRow(Crystarium.FormScope form, OverlayReading node)
    {
        form.NumericSlider(
            "Text size",
            node.State.FontSize,
            OverlayNodeLimits.MinFontSize,
            OverlayNodeLimits.MaxFontSize,
            next => _values.SetFontSize(node.Id, (uint)MathF.Round(next)),
            perPixel: 0.2f,
            format: "0",
            help: "Point size of the drawn text");
    }

    private void LifetimeRows(
        Crystarium.FormScope form, OverlayReading node)
    {
        form.Actions(string.Empty, actions =>
        {
            actions.Button("Save to library", () => _names.Open(
                    "Save overlay to library", node.State.Name,
                    name =>
                    {
                        if (_values.Read(node.Id) != null)
                            _scenePane.SaveOverlayEntry(node.Id.LogicalId, name);
                    }));
            actions.Button(
                "Duplicate",
                () => _pending = () => Duplicate(node.Id),
                help: "Duplicate this overlay");
            actions.Button(
                "Delete",
                () =>
                {
                    _pending = () => _ = _entityActions.Remove(SelectionId.ForOverlay(node.Id));
                },
                variant: ButtonVariant.Danger,
                help: "Take this overlay off the screen");
        });
        if (_status.Length > 0) form.Status(_status);
    }

    // ── the icon picker ──────────────────────────────────────────────────

    private OverlayId? _iconTarget;

    private void OpenIconPicker(OverlayReading node)
    {
        _iconTarget = node.Id;
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
            node.State.StatusIconId.ToString(
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
        if (_iconTarget is { } id)
            _values.SetStatusIconId(id, choice.IconId);
        _iconTarget = null;
    }

    // ── acts ─────────────────────────────────────────────────────────────

    /// <summary>Public because the overlay row's context menu speaks this
    /// verb too — one duplication rule, wherever it is asked.</summary>
    private void Duplicate(OverlayId id)
    {
        var result = _creation.Duplicate(SelectionId.ForOverlay(id));
        if (result.Handle is { } copy)
        {
            SelectWhenBound(copy);
            _status = string.Empty;
        }
        else _status = result.Detail ?? "The overlay could not be duplicated.";
    }

    private static Vector2 Centred(OverlayReading node)
    {
        var viewport = ImGui.GetMainViewport().Size;
        // The same extent the node layer gives the game as the node's own size,
        // so the middle a node is centred on is the middle you can grab it by.
        var extent = OverlayNodeGeometry.DesignSize(node.State.Kind) * node.State.Scale;
        return (viewport - extent) * 0.5f;
    }

    // ── state ────────────────────────────────────────────────────────────

    private OverlayReading? SelectedNode()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId })
            return null;
        return _values.Read(overlayId);
    }

    /// <summary>Second half of <see cref="SelectWhenBound"/>: once the scene
    /// refresh has bound the new node, select it and forget it.</summary>
    private void ReconcilePendingSelect()
    {
        _pendingSelect.Reconcile(receipt => _creation.Resolve(receipt), _scene.Selection);
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
