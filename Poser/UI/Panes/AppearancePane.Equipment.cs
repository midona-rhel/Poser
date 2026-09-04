using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// The Equipment view: what the actor wears, through Glamourer. A card
/// per slot — the icon two rows tall opens the item picker; the name
/// reads beside it and the two dyes fill the line under the name as
/// colour boxes — a Prop verb under each weapon; the facewear; the
/// visibility switches; the outfit verbs; the raw model ids, closed. Every
/// change is one journal step through
/// <see cref="Game.Journal.WardrobeSession"/>. Without Glamourer
/// everything disables in place and says why.
/// </summary>
public sealed partial class AppearancePane
{
    private const float CardTile = 2f;

    /// <summary>The Equipment view's label column: its labels are short
    /// by design so the controls get the width.</summary>
    private const float EquipmentLabelWidth = 56f;

    private static readonly EquipSlot[] LeftTrack =
    {
        EquipSlot.MainHand, EquipSlot.Head, EquipSlot.Body,
        EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
    };

    private static readonly EquipSlot[] RightTrack =
    {
        EquipSlot.OffHand, EquipSlot.Ears, EquipSlot.Neck,
        EquipSlot.Wrists, EquipSlot.RightFinger, EquipSlot.LeftFinger,
    };

    private static readonly EquipSlot[] Stacked =
    {
        EquipSlot.MainHand, EquipSlot.OffHand, EquipSlot.Head, EquipSlot.Body,
        EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet, EquipSlot.Ears,
        EquipSlot.Neck, EquipSlot.Wrists, EquipSlot.RightFinger, EquipSlot.LeftFinger,
    };

    private bool _openGear = true;
    private bool _openModelIds;

    private static readonly TimeSpan WardrobeInterval = TimeSpan.FromSeconds(1);
    private ActorId? _wardrobeActor;
    private DateTime _wardrobeAt = DateTime.MinValue;
    private WardrobeState? _wardrobeState;
    private string? _wardrobeDetail;

    private readonly Crystarium.SearchPicker<WardrobeItem> _itemPicker = new("appearance-item");
    private readonly Crystarium.SearchPicker<DyeEntry> _dyePicker = new("appearance-dye");
    private readonly Crystarium.SearchPicker<FacewearEntry> _facewearPicker = new("appearance-facewear");
    private readonly Crystarium.SearchPicker<PropRow> _propPicker = new("appearance-prop");
    private ActorId? _wardrobePickerActor;
    private EquipSlot _pickerSlot;
    private int _pickerDye;

    private string? _itemMemoQuery;
    private EquipSlot _itemMemoSlot;
    private IReadOnlyList<WardrobeItem> _itemMemo = Array.Empty<WardrobeItem>();
    private List<PropRow>? _propRows;
    private List<DyeEntry>? _dyeRows;
    private List<FacewearEntry>? _facewearRows;
    private static readonly DyeEntry NoDye = new(0, "None", 0);
    private static readonly FacewearEntry NoFacewear = new(0, "None", 0);

    private static readonly Func<WardrobeItem, string> WardrobeItemName = static item => item.Name;
    private static readonly Func<WardrobeItem, string> WardrobeItemKey =
        static item => item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Func<DyeEntry, string> DyeName = static dye => dye.Name;
    private static readonly Func<DyeEntry, string> DyeKey =
        static dye => dye.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Func<FacewearEntry, string> FacewearName = static entry => entry.Name;
    private static readonly Func<FacewearEntry, string> FacewearKey =
        static entry => entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Func<PropRow, string> PropName = static row => row.Name;
    private static readonly Func<PropRow, string> PropKey = static row => row.Key;

    /// <summary>A prop as a picker row: the search picker wants a class.</summary>
    private sealed record PropRow(string Name, string Key, string Detail, PropModel Model);

    /// <summary>Raw model ids typed per slot, applied on commit.</summary>
    private readonly Dictionary<EquipSlot, int[]> _idDrafts = new();
    private ActorId? _idDraftActor;

    // ── the view ────────────────────────────────────────────────────────

    private void DrawEquipmentView(Crystarium.PageScope page, ActorId actor)
    {
        var glamourer = _integration.Glamourer;
        bool ready = glamourer.Available && _appearanceAccess.CanEdit;
        string? blocked = !_appearanceAccess.CanEdit ? _appearanceAccess.Detail : ready ? null : glamourer.Detail;
        var state = ready ? ReadWardrobe(actor) : null;

        page.Section("Gear", _openGear, next => _openGear = next, form =>
        {
            if (!ready && _appearanceAccess.CanEdit)
                form.Status(blocked);
            else if (ready && state is null && _wardrobeDetail is { } detail)
                form.Status(detail);
            form.PairRows();
            if (form.TwoTrack)
            {
                for (int i = 0; i < LeftTrack.Length; i++)
                {
                    if (i > 0)
                        form.Divider();
                    ItemRow(form, actor, LeftTrack[i], state, ready, blocked);
                    ItemRow(form, actor, RightTrack[i], state, ready, blocked);
                    if (i == 0)
                    {
                        PropVerbRow(form, actor, LeftTrack[i], ready, blocked);
                        PropVerbRow(form, actor, RightTrack[i], ready, blocked);
                    }
                }
            }
            else
            {
                for (int i = 0; i < Stacked.Length; i++)
                {
                    if (i > 0)
                        form.Divider();
                    ItemRow(form, actor, Stacked[i], state, ready, blocked);
                    if (i < 2)
                        PropVerbRow(form, actor, Stacked[i], ready, blocked);
                }
            }
            form.Divider();
            form.FullLine();
            FacewearRow(form, actor, state, ready, blocked);
            form.EndPair();
            form.Checkboxes("Show",
                new Crystarium.CheckItem("Hat", state?.HatVisible ?? true,
                    on => Switch(actor, MetaSwitch.HatVisible, on),
                    ready ? "Show the headgear" : blocked, !ready),
                new Crystarium.CheckItem("Visor", state?.VisorToggled ?? false,
                    on => Switch(actor, MetaSwitch.VisorToggled, on),
                    ready ? "Flip the visor" : blocked, !ready),
                new Crystarium.CheckItem("Weapon", state?.WeaponVisible ?? true,
                    on => Switch(actor, MetaSwitch.WeaponVisible, on),
                    ready ? "Show the weapons" : blocked, !ready));
            // The outfits are a labelled row of verbs, not a section.
            form.Actions("Outfit", actions =>
            {
                actions.Button("Remove all",
                    () => Outfit(actor, "Remove all", static _ => new WardrobeSlot(0, 0, 0)),
                    disabled: !ready, help: ready ? "Take everything off" : blocked);
                actions.Button("Smallclothes",
                    () => Outfit(actor, "Wear smallclothes", SmallclothesFor),
                    disabled: !ready, help: ready ? "The NPC smallclothes" : blocked);
                actions.Button("Emperor's",
                    () => Outfit(actor, "Wear the Emperor's set", EmperorsFor),
                    disabled: !ready, help: ready ? "The Emperor's New set" : blocked);
                actions.Button("Invisible",
                    () => Outfit(actor, "Wear invisible clothes", InvisibleFor),
                    disabled: !ready, help: ready ? "Clothes that do not draw" : blocked);
            }, help: "Dress every slot at once");
        }, divider: false);

        page.Section("Model ids", _openModelIds, next => _openModelIds = next, form =>
        {
            foreach (var slot in Stacked)
                ModelIdRow(form, actor, slot, state, ready, blocked);
        });
    }

    // ── rows ────────────────────────────────────────────────────────────

    /// <summary>The slot's card: the icon two rows tall opens the item
    /// picker; beside it the item's name reads on the first line and the
    /// two dyes fill the second as colour boxes that open the dye picker.
    /// The card holds no verbs: Ctrl-click on the icon takes the item
    /// off and Ctrl-click on a dye box clears that dye (the rule), "None"
    /// leads the dye list, and clothes come off through Remove all.</summary>
    private void ItemRow(
        Crystarium.FormScope form, ActorId actor, EquipSlot slot,
        WardrobeState? state, bool ready, string? blocked)
    {
        var theme = Crystarium.ActiveTheme;
        float tile = theme.Controls.FormRowHeight * CardTile;
        form.Custom(SlotName(slot), tile, row =>
        {
            float s = row.Scale;
            float gap = theme.Page.ActionGap * s;
            float side = tile * s;
            float half = side * 0.5f;
            var origin = row.ControlOrigin;
            WardrobeSlot? worn = state?.Slot(slot);
            var item = worn is { } w && WardrobeIds.IsSheetItem(w.ItemId)
                ? _wardrobe.Item((uint)w.ItemId)
                : null;

            ImGui.SetCursorScreenPos(origin);
            Crystarium.ImageTile(
                $"wardrobe-{slot}-tile",
                ResolveIcon(item?.Icon ?? FallbackIcon(slot)),
                tile,
                () =>
                {
                    if (ImGui.GetIO().KeyCtrl)
                    {
                        if (worn is { } w4 && !WardrobeIds.IsNothing(w4.ItemId))
                            SetItem(actor, slot, 0, w4.Dye1, w4.Dye2,
                                $"Remove {SlotName(slot).ToLowerInvariant()}");
                    }
                    else
                        OpenItemPicker(actor, slot);
                },
                help: ready ? "Choose an item" : blocked,
                disabled: !ready);

            float x = origin.X + side + gap;
            float width = MathF.Max(1f, row.ControlWidth - side - gap);
            var nameStyle = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Color = theme.Text,
                Disabled = !ready,
            };
            Crystarium.TextInBand(
                new Vector2(x, origin.Y),
                new Vector2(width, half),
                Crystarium.TruncateText(
                    worn is { } w2 ? WornName(w2, item) : "—", nameStyle, width),
                nameStyle);

            bool empty = worn is not { } w3 || WardrobeIds.IsNothing(w3.ItemId);
            bool dyeable = ready && !empty;
            string? why = !ready ? blocked : empty ? "Nothing is worn here" : null;
            float square = theme.Controls.WorkspaceHeight;
            float second = origin.Y + half + (half - square * s) * 0.5f;
            float dyeW = MathF.Max(1f, (width - gap) * 0.5f);
            for (int which = 0; which < 2; which++)
            {
                byte dyeId = worn is { } d ? (which == 0 ? d.Dye1 : d.Dye2) : (byte)0;
                var dye = dyeId != 0 ? _wardrobe.Dye(dyeId) : null;
                int index = which;
                ImGui.SetCursorScreenPos(new Vector2(x + which * (dyeW + gap), second));
                Crystarium.ColorTile(
                    $"wardrobe-{slot}-dye{which}",
                    dye is { } paint ? DyeColor(paint.Color) : null,
                    dyeW / s,
                    square,
                    () =>
                    {
                        if (ImGui.GetIO().KeyCtrl)
                        {
                            if (dyeId != 0)
                                SetDye(actor, slot, index, 0);
                        }
                        else
                            OpenDyePicker(actor, slot, index);
                    },
                    label: dye is null ? "None" : null,
                    help: dyeable ? (dye?.Name ?? (which == 0 ? "Choose the first dye" : "Choose the second dye")) : why,
                    disabled: !dyeable);
            }
        }, help: SlotHelp(slot));
    }

    /// <summary>Under a weapon's card, the one verb: a prop in its place.</summary>
    private void PropVerbRow(
        Crystarium.FormScope form, ActorId actor, EquipSlot slot,
        bool ready, string? blocked)
    {
        var theme = Crystarium.ActiveTheme;
        form.Custom(string.Empty, theme.Controls.FormRowHeight, row =>
        {
            ImGui.SetCursorScreenPos(row.CenterControl(theme.Controls.WorkspaceHeight));
            Crystarium.Button("Prop",
                () => OpenPropPicker(actor, slot),
                style: ControlStyle.Workspace with { Width = UiWidth.Fixed(theme.Form.VerbWidth) },
                disabled: !ready,
                help: ready ? "Wear a prop instead" : blocked,
                id: $"wardrobe-{slot}-prop");
        });
    }

    private void FacewearRow(
        Crystarium.FormScope form, ActorId actor,
        WardrobeState? state, bool ready, string? blocked)
    {
        var theme = Crystarium.ActiveTheme;
        float tile = theme.Controls.FormRowHeight * CardTile;
        form.Custom("Face", tile, row =>
        {
            float s = row.Scale;
            float gap = theme.Page.ActionGap * s;
            float side = tile * s;
            float half = side * 0.5f;
            var origin = row.ControlOrigin;
            ulong worn = state?.Facewear ?? 0;
            var entry = WardrobeIds.IsNoFacewear(worn) ? null : FacewearById(worn);

            ImGui.SetCursorScreenPos(origin);
            Crystarium.ImageTile(
                "wardrobe-facewear-tile",
                ResolveIcon(entry?.Icon ?? FallbackIcon(null)),
                tile,
                () =>
                {
                    if (ImGui.GetIO().KeyCtrl)
                    {
                        if (entry is not null)
                            SetFacewear(actor, 0, "Remove facewear");
                    }
                    else
                        OpenFacewearPicker(actor);
                },
                help: ready ? "Choose a facewear" : blocked,
                disabled: !ready);

            float x = origin.X + side + gap;
            float width = MathF.Max(1f, row.ControlWidth - side - gap);
            var nameStyle = new TextStyle
            {
                Size = theme.Typography.LabelSize,
                Color = theme.Text,
                Disabled = !ready,
            };
            Crystarium.TextInBand(
                new Vector2(x, origin.Y),
                new Vector2(width, half),
                Crystarium.TruncateText(
                    state is null ? "—" : entry?.Name ?? "None", nameStyle, width),
                nameStyle);
        }, help: "Glasses and other facewear");
    }

    /// <summary>The raw ids behind a slot: model, weapon type on weapons,
    /// variant. A committed well wears the ids as they stand.</summary>
    private void ModelIdRow(
        Crystarium.FormScope form, ActorId actor, EquipSlot slot,
        WardrobeState? state, bool ready, string? blocked)
    {
        var theme = Crystarium.ActiveTheme;
        bool weapon = slot is EquipSlot.MainHand or EquipSlot.OffHand;
        form.Custom(SlotName(slot), theme.Controls.FormRowHeight, row =>
        {
            float s = row.Scale;
            float tight = theme.Spacing.One * s;
            float wellW = theme.Form.AxisWellMinimumWidth;
            var draft = Draft(actor, slot, state);
            var seat = row.CenterControl(theme.Controls.WorkspaceHeight);
            float x = seat.X;
            int count = weapon ? 3 : 2;
            for (int i = 0; i < count; i++)
            {
                int index = weapon ? i : (i == 0 ? 0 : 2);
                ImGui.SetCursorScreenPos(new Vector2(x, seat.Y));
                Crystarium.AxisWell(
                    $"wardrobe-{slot}-id{index}",
                    string.Empty,
                    draft[index],
                    next => draft[index] = Math.Max(0, (int)MathF.Round(next)),
                    () => ApplyDraft(actor, slot, draft),
                    theme.FormValue,
                    0.25f,
                    "0",
                    ControlStyle.Workspace with { Width = UiWidth.Fixed(wellW) },
                    disabled: !ready);
                x += wellW * s + tight;
            }
        }, help: ready
            ? (weapon ? "Model, weapon type, variant" : "Model and variant")
            : blocked);
    }

    // ── state ───────────────────────────────────────────────────────────

    private WardrobeState? ReadWardrobe(ActorId actor)
    {
        var now = DateTime.UtcNow;
        if (_wardrobeActor is { } cached && cached.Equals(actor)
            && now - _wardrobeAt < WardrobeInterval)
            return _wardrobeState;
        _wardrobeActor = actor;
        _wardrobeAt = now;
        var read = _wardrobeSession.Read(actor);
        _wardrobeState = read.Success ? read.Value : null;
        _wardrobeDetail = read.Success ? null : read.Detail;
        return _wardrobeState;
    }

    private void InvalidateWardrobe()
    {
        _wardrobeAt = DateTime.MinValue;
        _idDraftActor = null;
    }

    private int[] Draft(ActorId actor, EquipSlot slot, WardrobeState? state)
    {
        if (_idDraftActor is not { } owner || !owner.Equals(actor))
        {
            _idDrafts.Clear();
            _idDraftActor = actor;
        }
        if (_idDrafts.TryGetValue(slot, out var draft))
            return draft;
        draft = new int[3];
        if (state?.Slot(slot) is { } worn)
        {
            var (model, type, variant) = ModelIdsOf(worn);
            draft[0] = model;
            draft[1] = type;
            draft[2] = variant;
        }
        _idDrafts[slot] = draft;
        return draft;
    }

    private (int Model, int Type, int Variant) ModelIdsOf(WardrobeSlot worn)
    {
        if (WardrobeIds.IsCustom(worn.ItemId))
        {
            var (model, type, variant) = WardrobeIds.Split(worn.ItemId);
            return (model, type, variant);
        }
        if (WardrobeIds.IsSheetItem(worn.ItemId) && _wardrobe.Item((uint)worn.ItemId) is { } item)
            return (item.Model, item.WeaponType, item.Variant);
        return (0, 0, 0);
    }

    private void ApplyDraft(ActorId actor, EquipSlot slot, int[] draft)
    {
        var worn = ReadWardrobe(actor)?.Slot(slot) ?? default;
        if (draft[0] == 0)
        {
            SetItem(actor, slot, 0, worn.Dye1, worn.Dye2, $"Remove {SlotName(slot).ToLowerInvariant()}");
            return;
        }
        ulong id = WardrobeIds.Custom(
            (ushort)Math.Clamp(draft[0], 0, ushort.MaxValue),
            (ushort)Math.Clamp(draft[1], 0, ushort.MaxValue),
            (byte)Math.Clamp(draft[2], 0, byte.MaxValue));
        SetItem(actor, slot, id, worn.Dye1, worn.Dye2, $"Set {SlotName(slot).ToLowerInvariant()} model");
    }

    // ── writes ──────────────────────────────────────────────────────────

    private void SetItem(ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2, string description)
    {
        ReportExternal(_wardrobeSession.SetItem(actor, slot, itemId, dye1, dye2, description), description);
        InvalidateWardrobe();
    }

    private void SetDye(ActorId actor, EquipSlot slot, int which, byte dye)
    {
        string description = dye == 0
            ? $"Clear {SlotName(slot).ToLowerInvariant()} dye {which + 1}"
            : $"Dye {SlotName(slot).ToLowerInvariant()}";
        ReportExternal(_wardrobeSession.SetDye(actor, slot, which, dye, description), description);
        InvalidateWardrobe();
    }

    private void SetFacewear(ActorId actor, ulong id, string description)
    {
        ReportExternal(_wardrobeSession.SetFacewear(actor, id, description), description);
        InvalidateWardrobe();
    }

    private void Switch(ActorId actor, MetaSwitch which, bool on)
    {
        ReportExternal(_wardrobeSession.SetSwitch(actor, which, on), "Show");
        InvalidateWardrobe();
    }

    private void Outfit(ActorId actor, string description, Func<EquipSlot, WardrobeSlot?> outfit)
    {
        ReportExternal(_wardrobeSession.SetOutfit(actor, description, outfit), description);
        InvalidateWardrobe();
    }

    private static bool IsBody(EquipSlot slot) =>
        slot is EquipSlot.Head or EquipSlot.Body or EquipSlot.Hands or EquipSlot.Legs or EquipSlot.Feet;

    private static bool IsAccessory(EquipSlot slot) =>
        slot is EquipSlot.Ears or EquipSlot.Neck or EquipSlot.Wrists
            or EquipSlot.RightFinger or EquipSlot.LeftFinger;

    private static WardrobeSlot? SmallclothesFor(EquipSlot slot) =>
        IsBody(slot) ? new WardrobeSlot(WardrobeIds.Smallclothes(slot), 0, 0)
        : IsAccessory(slot) ? new WardrobeSlot(0, 0, 0)
        : null;

    private static WardrobeSlot? EmperorsFor(EquipSlot slot) =>
        IsBody(slot) ? new WardrobeSlot(WardrobeIds.EmperorsBody, 0, 0)
        : IsAccessory(slot) ? new WardrobeSlot(WardrobeIds.EmperorsAccessory, 0, 0)
        : null;

    private static WardrobeSlot? InvisibleFor(EquipSlot slot) =>
        slot is EquipSlot.Head or EquipSlot.Body ? new WardrobeSlot(WardrobeIds.Invisible, 0, 0)
        : IsBody(slot) ? new WardrobeSlot(WardrobeIds.EmperorsBody, 0, 0)
        : IsAccessory(slot) ? new WardrobeSlot(WardrobeIds.EmperorsAccessory, 0, 0)
        : null;

    /// <summary>Saves the look as a Glamourer design under a typed name.</summary>
    private void SaveDesign(ActorId actor)
    {
        string suggested = _scene.Snapshot.FindActor(actor) is { } described
            ? ActorNames.Display(described)
            : "Design";
        _names.Open("Save design", suggested, name =>
        {
            var saved = _integration.SaveActorDesign(actor, name);
            if (saved.Success)
                _notices.Done($"Saved design '{name}'.");
            else if (saved.AppearanceRefusal is GlamourerAccessKind.ForeignHeld or GlamourerAccessKind.PoserHeld)
                _accessAt = DateTime.MinValue;
            else
                _notices.Failed($"Save design: {saved.Detail}");
        }, placeholder: "Design name");
    }

    /// <summary>Revert hands the actor back to the game; its inverse puts
    /// the look read before the revert back on.</summary>
    private void RevertLook(ActorId actor)
    {
        var before = _integration.GetStateJson(actor);
        Func<IntegrationResult>? inverse = before.Success && before.Value is { } json
            ? () => _integration.ApplyStateJson(actor, json)
            : null;
        ReportExternal(_disruptive.Run(actor, "Revert look",
            () => _integration.RevertState(actor), inverse), "Revert");
        InvalidateWardrobe();
    }

    // ── pickers ─────────────────────────────────────────────────────────

    private void OpenItemPicker(ActorId actor, EquipSlot slot)
    {
        _wardrobePickerActor = actor;
        _pickerSlot = slot;
        _itemMemoQuery = null;
        var worn = ReadWardrobe(actor)?.Slot(slot);
        string? selected = worn is { } w && WardrobeIds.IsSheetItem(w.ItemId)
            ? w.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        _itemPicker.Open(
            SlotName(slot),
            Array.Empty<WardrobeItem>(),
            WardrobeItemName,
            WardrobeItemKey,
            selected,
            null,
            new PickerOptions<WardrobeItem>
            {
                Query = ItemSearch,
                Texture = _wardrobeItemTexture,
                Badge = _wardrobeItemBadge,
                Width = Crystarium.ActiveTheme.Picker.WideWidth,
            });
    }

    /// <summary>Items by name, or by id when the search is a number.</summary>
    private IReadOnlyList<WardrobeItem> ItemSearch(string search)
    {
        if (_itemMemoQuery == search && _itemMemoSlot == _pickerSlot)
            return _itemMemo;
        _itemMemoQuery = search;
        _itemMemoSlot = _pickerSlot;
        var all = _wardrobe.ItemsFor(_pickerSlot);
        string trimmed = search.Trim();
        if (trimmed.Length == 0)
            return _itemMemo = all;
        bool byId = uint.TryParse(
            trimmed, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint id);
        var found = new List<WardrobeItem>();
        foreach (var item in all)
        {
            if (byId && item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    .StartsWith(trimmed, StringComparison.Ordinal))
                found.Add(item);
            else if (item.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                found.Add(item);
        }
        if (byId)
            found.Sort((a, b) => a.Id == id ? -1 : b.Id == id ? 1 : 0);
        return _itemMemo = found;
    }

    private void OpenDyePicker(ActorId actor, EquipSlot slot, int which)
    {
        _wardrobePickerActor = actor;
        _pickerSlot = slot;
        _pickerDye = which;
        var worn = ReadWardrobe(actor)?.Slot(slot);
        byte current = worn is { } w ? (which == 0 ? w.Dye1 : w.Dye2) : (byte)0;
        if (_dyeRows is null)
        {
            _dyeRows = new List<DyeEntry> { NoDye };
            _dyeRows.AddRange(_wardrobe.Dyes);
        }
        _dyePicker.Open(
            which == 0 ? "Dye 1" : "Dye 2",
            _dyeRows,
            DyeName,
            DyeKey,
            current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            new PickerOptions<DyeEntry> { RowFill = _dyeRowFill });
    }

    private void OpenFacewearPicker(ActorId actor)
    {
        _wardrobePickerActor = actor;
        ulong worn = ReadWardrobe(actor)?.Facewear ?? 0;
        if (_facewearRows is null)
        {
            _facewearRows = new List<FacewearEntry> { NoFacewear };
            _facewearRows.AddRange(_wardrobe.Facewear);
        }
        _facewearPicker.Open(
            "Facewear",
            _facewearRows,
            FacewearName,
            FacewearKey,
            FacewearById(worn) is { } known
                ? known.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0",
            null,
            new PickerOptions<FacewearEntry> { Texture = _facewearTexture });
    }

    private void OpenPropPicker(ActorId actor, EquipSlot slot)
    {
        _wardrobePickerActor = actor;
        _pickerSlot = slot;
        if (_propRows is null)
        {
            _propRows = new List<PropRow>();
            var models = _props.Catalog;
            for (int i = 0; i < models.Count; i++)
            {
                var model = models[i];
                _propRows.Add(new PropRow(
                    model.Name,
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"{model.Model}·{model.Submodel}·{model.Variant}",
                    model));
            }
        }
        _propPicker.Open(
            SlotName(slot),
            _propRows,
            PropName,
            PropKey,
            null,
            _propRows.Count == 0 ? "No props are known." : null,
            new PickerOptions<PropRow> { Badge = _propBadge, Glyph = _propGlyph });
    }

    /// <summary>Runs the wardrobe pickers and lands their picks on the
    /// actor captured when they opened.</summary>
    private void DrainWardrobePickers()
    {
        if (_wardrobePickerActor is not { } actor)
        {
            _itemPicker.Draw();
            _dyePicker.Draw();
            _facewearPicker.Draw();
            _propPicker.Draw();
            return;
        }
        if (_itemPicker.Draw() is { } item)
        {
            var worn = ReadWardrobe(actor)?.Slot(_pickerSlot) ?? default;
            SetItem(actor, _pickerSlot, item.Item.Id,
                item.Item.DyeCount > 0 ? worn.Dye1 : (byte)0,
                item.Item.DyeCount > 1 ? worn.Dye2 : (byte)0,
                $"Wear {item.Item.Name}");
        }
        if (_dyePicker.Draw() is { } dye)
            SetDye(actor, _pickerSlot, _pickerDye, dye.Item.Id);
        if (_facewearPicker.Draw() is { } facewear)
            SetFacewear(actor, facewear.Item.Id,
                facewear.Item.Id == 0 ? "Remove facewear" : $"Wear {facewear.Item.Name}");
        if (_propPicker.Draw() is { } prop)
        {
            var model = prop.Item.Model;
            SetItem(actor, _pickerSlot,
                WardrobeIds.Custom(model.Model, model.Submodel, model.Variant),
                model.Stain0, model.Stain1, $"Wear {prop.Item.Name}");
        }
    }

    // ── readouts ────────────────────────────────────────────────────────

    private static string SlotName(EquipSlot slot) => slot switch
    {
        EquipSlot.MainHand => "Main",
        EquipSlot.OffHand => "Off",
        EquipSlot.Head => "Head",
        EquipSlot.Body => "Body",
        EquipSlot.Hands => "Hands",
        EquipSlot.Legs => "Legs",
        EquipSlot.Feet => "Feet",
        EquipSlot.Ears => "Ears",
        EquipSlot.Neck => "Neck",
        EquipSlot.Wrists => "Wrists",
        EquipSlot.RightFinger => "R ring",
        EquipSlot.LeftFinger => "L ring",
        _ => "Slot",
    };

    private static string SlotHelp(EquipSlot slot) => slot switch
    {
        EquipSlot.MainHand => "The main hand weapon",
        EquipSlot.OffHand => "The off hand weapon or shield",
        _ => $"What the {SlotName(slot).ToLowerInvariant()} wears",
    };

    /// <summary>The game's own slot icons: what an empty slot shows.</summary>
    private static uint FallbackIcon(EquipSlot? slot) => slot switch
    {
        EquipSlot.MainHand => 60102,
        EquipSlot.OffHand => 60110,
        EquipSlot.Head => 60124,
        EquipSlot.Body => 60125,
        EquipSlot.Hands => 60129,
        EquipSlot.Legs => 60127,
        EquipSlot.Feet => 60130,
        EquipSlot.Ears => 60133,
        EquipSlot.Neck => 60132,
        EquipSlot.Wrists => 60134,
        EquipSlot.RightFinger or EquipSlot.LeftFinger => 60135,
        _ => 60189,
    };

    private static string WornName(WardrobeSlot worn, WardrobeItem? item)
    {
        if (item is not null)
            return item.Name;
        if (WardrobeIds.IsSmallclothes(worn.ItemId))
            return "Smallclothes";
        if (WardrobeIds.IsNothing(worn.ItemId))
            return "Nothing";
        if (WardrobeIds.IsCustom(worn.ItemId))
        {
            var (model, type, variant) = WardrobeIds.Split(worn.ItemId);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return $"Model {model.ToString(culture)}·{type.ToString(culture)}·{variant.ToString(culture)}";
        }
        return "Unknown item";
    }

    private FacewearEntry? FacewearById(ulong id)
    {
        if (id == 0 || id > uint.MaxValue)
            return null;
        foreach (var entry in _wardrobe.Facewear)
            if (entry.Id == (uint)id)
                return entry;
        return null;
    }

    /// <summary>A stain's packed RGB as a colour.</summary>
    private static Vector4 DyeColor(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);
}
