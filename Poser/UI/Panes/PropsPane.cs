using System;
using Poser.Game;
using Poser.Game.Scene;
using Poser.Services;
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
    private readonly IEntityBindings _bindings;
    private readonly StainCatalog _stains;

    /// <summary>The dye sheet's picker; the owner string carries which of
    /// the two channels is being chosen.</summary>
    private readonly Crystarium.SearchPicker<StainEntry> _dyePicker =
        new("prop-dye");

    private string _status = string.Empty;
    private IPropHandle? _animDraftFor;
    private float _animDraft;

    /// <summary>Destroying a prop is a scene-lifecycle act, so it goes through
    /// the seam that files one in the same history the transforms use — not
    /// through the spawn service, which owns the native object and no
    /// history.</summary>
    private readonly ISceneLifecycleHistory _lifecycle;

    private bool _openProp = true;

    /// <summary>Anything that changes the list, run after the page has drawn.
    /// </summary>
    private Action? _pending;
    private readonly Game.Journal.PropSession _values;

    public PropsPane(
        SceneSession scene,
        IEntityBindings bindings,
        ISceneLifecycleHistory lifecycle,
        StainCatalog stains,
        ScenePane scenePane,
        global::Poser.UI.Controls.EntityNameModal names,
        Game.Journal.PropSession values)
    {
        _scene = scene;
        _bindings = bindings;
        _lifecycle = lifecycle;
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
            && SelectedProp() is { } target)
        {
            int channel = picked.Owner.EndsWith("1", StringComparison.Ordinal)
                ? 1
                : 0;
            var next = channel == 0
                ? target.Model with { Stain0 = picked.Item.Id }
                : target.Model with { Stain1 = picked.Item.Id };
            _status = _values.SetModel(target, next, out var refusal)
                ? string.Empty
                : refusal ?? "The dye could not be applied.";
        }

        var pending = _pending;
        _pending = null;
        pending?.Invoke();
    }

    private void OpenDyePicker(IPropHandle prop, int channel)
    {
        byte current = channel == 0
            ? prop.Model.Stain0
            : prop.Model.Stain1;
        _dyePicker.Open(
            "prop-dye-" + channel,
            _stains.Entries,
            static stain => stain.Name,
            static stain => stain.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            loadError: _stains.Entries.Count <= 1
                ? "The dye sheet could not be read."
                : null);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void PropRows(Crystarium.FormScope form, IPropHandle prop)
    {
        // Identity first, the camera pattern: the name leads the page.
        form.TextInput(
            "Name",
            prop.Name,
            next => _values.SetName(prop, next),
            placeholder: "Object",
            help: "What the sidebar calls this object");
        form.Switch(
            "Visible",
            prop.Visible,
            next => _values.SetVisible(prop, next),
            help: "Hide this object without destroying it");
        // The dyes bake at creation, so choosing one respawns the weapon
        // in place — handle, name, and placement survive.
        form.Pair(
            "Dye",
            cell => cell.Picker(
                "##prop-dye-0",
                _stains.NameOf(prop.Model.Stain0),
                () => OpenDyePicker(prop, channel: 0),
                help: "Dye the model's first channel"),
            "Dye 2",
            cell => cell.Picker(
                "##prop-dye-1",
                _stains.NameOf(prop.Model.Stain1),
                () => OpenDyePicker(prop, channel: 1),
                help: "Dye the model's second channel"));
        // The variant edits a DRAFT and applies on release — a respawn
        // per drag tick would churn the weapon.
        if (!ReferenceEquals(_animDraftFor, prop))
        {
            _animDraftFor = prop;
            _animDraft = prop.Model.AnimationVariant;
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
                if (stated != prop.Model.AnimationVariant)
                    _status = prop.Respawn(
                        prop.Model with { AnimationVariant = stated },
                        out var refusal)
                        ? string.Empty
                        : refusal ?? "The variant could not be applied.";
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
                        if (_bindings.GetPropId(prop) is { } entryId)
                            _scenePane.SavePropEntry(
                                entryId.LogicalId, name);
                    });
                },
                help: "Save a spawnable copy of this prop", icon: TablerIcon.Dots);
        form.Actions("Lifetime", actions =>
        {
            // Destroy is THE destruction verb — Delete and Remove were
            // invented synonyms for the same act.
            actions.Button(
                "Destroy",
                () => _pending = () =>
                {
                    _lifecycle.DestroyProp(prop);
                    _scene.Selection.Clear();
                },
                variant: ButtonVariant.Danger,
                help: "Destroy this object");
        });
    }

    // ── state ────────────────────────────────────────────────────────────

    private IPropHandle? SelectedProp()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Prop, Prop: { } propId })
            return null;
        var resolved = _bindings.Resolve(propId);
        return resolved.Success && resolved.Value is { IsValid: true } prop
            ? prop
            : null;
    }
}
