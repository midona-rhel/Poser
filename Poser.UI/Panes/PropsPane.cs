using Poser.Application.Selection;
using System;
using Poser.Application.Presentation;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Application.Catalog;
using static Poser.UI.Widgets.PageForm;

namespace Poser.UI;

/// <summary>
/// The page for the selected object (a prop). The sidebar lists the objects;
/// this page shows one object's settings and the buttons to save or destroy
/// it.
///
/// <para>Destroy waits until the page has finished drawing. Removing the
/// object earlier would change the scene's list while it is being drawn.</para>
/// </summary>
public sealed class PropsPane
{
    public Action? RequestDestroyAll { get; set; }
    private readonly SceneSession _scene;
    private readonly SelectionScope _selection;
    private readonly IWardrobeCatalog _stains;

    /// <summary>The dye picker. Its id ends in 0 or 1 to say which of the two
    /// dyes is being chosen.</summary>
    private readonly Controls.DyePicker _dyePicker =
        new("prop-dye");

    private string _status = string.Empty;
    private PropId? _animDraftFor;
    private PropId? _dyeTarget;
    private float _animDraft;
    private byte _animBaseline;

    private readonly EntityActions _entityActions;

    private bool _openProp = true;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;
    private readonly ISceneObjectControl _values;

    public PropsPane(
        SceneSession scene,
        PropertiesContext properties,
        EntityActions entityActions,
        IWardrobeCatalog stains,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        ISceneObjectControl values)
    {
        _scene = scene;
        _selection = properties.Selection;
        _entityActions = entityActions;
        _stains = stains;
        _values = values;
        _scenePane = scenePane;
        _names = names;
    }

    private readonly ScenePane _scenePane;
    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    public void Draw(Vector2 origin, Vector2 size)
    {
        Page("prop", origin, size, page =>
        {
            if (SelectedProp() is not { } prop)
            {
                page.EmptyState("Select an object in the sidebar.");
                return;
            }

            // Position, rotation and scale are in the inspector, as for a
            // light. This page shows only what the inspector does not.
            page.Section(
                "Object",
                _openProp,
                next => _openProp = next,
                form => PropRows(form, prop),
                divider: false);
        });

        // The dye picker is drawn after the page, as on the overlay page.
        if (_dyePicker.Draw() is { } picked
            && _dyeTarget is { } targetId
            && SelectedProp() is { } target && target.Id == targetId)
        {
            int channel = picked.Owner.EndsWith("1", StringComparison.Ordinal)
                ? 1
                : 0;
            var next = channel == 0
                ? target.Model with { Stain0 = picked.Item.Id }
                : target.Model with { Stain1 = picked.Item.Id };
            var result = _values.SetModel(targetId, next);
            _status = result.Success
                ? string.Empty
                : result.Detail ?? "The dye could not be applied.";
        }

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    private void OpenDyePicker(PropReading prop, int channel)
    {
        _dyeTarget = prop.Id;
        byte current = channel == 0
            ? prop.Model.Stain0
            : prop.Model.Stain1;
        _dyePicker.Open("prop-dye-" + channel, _stains, current);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void PropRows(FormScope form, PropReading prop)
    {
        // The name comes first, as on the camera page.
        form.TextInput(
            "Name",
            prop.Name,
            next => _values.Set(prop.Id, PropProperties.Name, next),
            placeholder: "Object",
            help: "What the sidebar calls this object");
        form.Switch(
            "Visible",
            prop.Visible,
            next => _values.Set(prop.Id, PropProperties.Visible, next),
            help: "Hide this object without destroying it");
        // The game sets dyes only when it creates the model, so choosing one
        // spawns the model again in the same place, keeping its name.
        form.Pair(
            "Dye",
            cell => cell.Picker(
                "##prop-dye-0",
                DyeName(prop.Model.Stain0),
                () => OpenDyePicker(prop, channel: 0),
                help: "Dye the model's first channel"),
            "Dye 2",
            cell => cell.Picker(
                "##prop-dye-1",
                DyeName(prop.Model.Stain1),
                () => OpenDyePicker(prop, channel: 1),
                help: "Dye the model's second channel"));
        form.ColorWells("Tint", wells => wells.Well(
                "Tint",
                prop.Tint,
                value => _values.Set(prop.Id, PropProperties.Tint, value)),
            help: "White leaves the model unchanged");
        // Dragging changes a draft value that is applied on release.
        // Applying every drag step would spawn the model again each time.
        if (_animDraftFor != prop.Id || _animBaseline != prop.Model.AnimationVariant)
        {
            _animDraftFor = prop.Id;
            _animDraft = _animBaseline = prop.Model.AnimationVariant;
        }
        form.Number(
            "Pose variant",
            _animDraft,
            next => _animDraft = MathF.Round(Math.Clamp(next, 0f, 255f)),
            perPixel: 0.05f,
            format: "0",
            help: "The model's animation variant; applies on release",
            onCommit: () =>
            {
                byte stated = (byte)_animDraft;
                if (_values.Read(prop.Id) is { } current && stated != current.Model.AnimationVariant)
                {
                    var result = _values.SetModel(prop.Id,
                        current.Model with { AnimationVariant = stated });
                    _status = result.Success ? string.Empty
                        : result.Detail ?? "The variant could not be applied.";
                    _animDraftFor = null;
                }
            });
        if (_status.Length > 0)
            form.Status(_status, warning: true);
        form.Actions("Library", actions => actions.Button("Save to library", () => _names.Open(
            "Save prop to library", prop.Name,
            name =>
            {
                if (_values.Read(prop.Id) is not null)
                    _scenePane.SaveEntry(SelectionId.ForProp(prop.Id), name);
            }), help: "Save a spawnable copy of this prop"));
        form.Actions("Lifetime", actions =>
        {
            actions.Button(
                "Destroy",
                () =>
                {
                    _pending = () => _ = _entityActions.Remove(SelectionId.ForProp(prop.Id));
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this object");
            actions.Button("Destroy all objects…", () => RequestDestroyAll?.Invoke(),
                variant: ButtonVariant.Danger);
        }, alignRight: true);
    }

    // ── state ────────────────────────────────────────────────────────────

    private string DyeName(byte id) => id == 0 ? "None" : _stains.Dye(id)?.Name ?? "Dye " + id;

    private PropReading? SelectedProp()
    {
        if (_selection.Primary is not
            { Kind: SceneEntityKind.Prop, Prop: { } propId })
            return null;
        return _values.Read(propId);
    }
}
