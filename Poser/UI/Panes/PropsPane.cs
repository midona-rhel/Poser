using System;
using Poser.Services;
using Poser.Application.Presentation;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>
/// The selected prop's editor — the pane behind the "Prop" tab that stands
/// while a PROPS sidebar row is selected. The sidebar owns the list (rows,
/// eye, the header's plus); this pane owns one prop: its visibility, its
/// lifetime, and its transform.
///
/// <para>Lifetime clicks are DEFERRED to the end of the frame: destroying the
/// prop republishes the scene mid-walk otherwise.</para>
/// </summary>
public sealed class PropsPane
{
    public Action? RequestDestroyAll { get; set; }
    private readonly SceneSession _scene;
    private readonly IWardrobeCatalog _stains;

    /// <summary>The dye sheet's picker; the owner string carries which of
    /// the two channels is being chosen.</summary>
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
        EntityActions entityActions,
        IWardrobeCatalog stains,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        ISceneObjectControl values)
    {
        _scene = scene;
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
        Crystarium.Page("prop", origin, size, page =>
        {
            if (SelectedProp() is not { } prop)
            {
                page.EmptyState("Select an object in the sidebar.");
                return;
            }

            // Transform lives on the inspector rail, exactly as a light's
            // does; this pane owns only what the rail cannot say.
            page.Section(
                "Object",
                _openProp,
                next => _openProp = next,
                form => PropRows(form, prop),
                divider: false);
        });

        // Pumped after the page — the overlay pane's rule.
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

    private void PropRows(Crystarium.FormScope form, PropReading prop)
    {
        // Identity first, the camera pattern: the name leads the page.
        form.TextInput(
            "Name",
            prop.Name,
            next => _values.SetName(prop.Id, next),
            placeholder: "Object",
            help: "What the sidebar calls this object");
        form.Switch(
            "Visible",
            prop.Visible,
            next => _values.SetVisible(prop.Id, next),
            help: "Hide this object without destroying it");
        // The dyes bake at creation, so choosing one respawns the weapon
        // in place — handle, name, and placement survive.
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
        // The variant edits a DRAFT and applies on release — a respawn
        // per drag tick would churn the weapon.
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
        form.ActionDropdown("More", ["Save to library", "Destroy all objects…"], -1, "More",
                choice =>
                {
                    if (choice == 1)
                    {
                        RequestDestroyAll?.Invoke();
                        return;
                    }
                    _names.Open(
                    "Save prop to library", prop.Name,
                    name =>
                    {
                        if (_values.Read(prop.Id) is not null)
                            _scenePane.SavePropEntry(
                                prop.Id.LogicalId, name);
                    });
                },
                help: "Save a spawnable copy of this prop", icon: TablerIcon.Dots);
        form.Actions("Lifetime", actions =>
        {
            // Destroy is THE destruction verb — Delete and Remove were
            // invented synonyms for the same act.
            actions.Button(
                "Destroy",
                () =>
                {
                    _pending = () => _ = _entityActions.Remove(SelectionId.ForProp(prop.Id));
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this object");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    private string DyeName(byte id) => id == 0 ? "None" : _stains.Dye(id)?.Name ?? "Dye " + id;

    private PropReading? SelectedProp()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Prop, Prop: { } propId })
            return null;
        return _values.Read(propId);
    }
}
