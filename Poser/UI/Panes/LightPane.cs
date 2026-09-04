using Poser.Domain.Transforms;
using System;
using Poser.Application.Viewport;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Config;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Services;
using DomainDelta = Poser.Domain.Transforms.TransformDelta;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainPivot = Poser.Domain.Transforms.PivotMode;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using GestureId = Poser.Domain.Transforms.TransformGestureId;

namespace Poser.UI;

/// <summary>
/// Light-scoped editor: emission, shadow casting, and the light's own
/// transform. The pane owns state and callbacks; Crystarium owns every row
/// and placement.
///
/// <para>Every property row writes the live <see cref="ILight"/> directly —
/// the lighting service re-runs the native update each tick, so a write is
/// the flush. The TRANSFORM rows are the exception: they drive the same
/// stable-id gesture lifecycle the pose inspector uses, so light moves join
/// undo history and the in-world gizmo.</para>
/// </summary>
public sealed class LightPane
{
    private readonly SceneSession _scene;
    private readonly IEntityBindings _bindings;
    private readonly ILightingService _lighting;

    /// <summary>Adding and removing a light goes through the lifecycle seam,
    /// so both land in the shell's undo history.</summary>
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly ILightFileService _lightFiles;
    private readonly ObjectPlacementPreferences _placement;
    private readonly IPlacementAnchorSource _anchors;
    private readonly ITransformFacade _cleanTransforms;
    private readonly IViewportReads _viewport;
    private readonly ICameraService _camera;
    private readonly ITextureProvider _textures;

    /// <summary>Where this pane's verb outcomes go; the page itself states
    /// standing facts only.</summary>
    private readonly UserNotices _notices;

    /// <summary>The destroy-all's first press. Held on the pane rather than on
    /// a light: it is a statement about the scene, so which light happens to
    /// be selected does not change what it means.</summary>
    private bool _destroyAllArmed;

    private bool _openGeneral = true;
    private bool _openLight = true;
    private bool _openShadows = true;
    private bool _openAttach = true;
    private bool _openFile = true;
    private bool _openActions = true;

    /// <summary>The gobo library's visual surface: the shared texture grid,
    /// walking the library by index with each tile captioned by NAME.</summary>
    private readonly Crystarium.TexturePicker _goboGrid;

    /// <summary>Every bone of every actor, flat and searchable — the attach
    /// target is one bone anywhere in the scene, not one bone of one actor.
    /// </summary>
    private readonly Crystarium.SearchPicker<BoneChoice> _attachPicker =
        new("light-attach");

    /// <summary>One picker row per bone, rebuilt at open: the surface's list is
    /// a snapshot of the scene at the moment it was asked for.</summary>
    private readonly List<BoneChoice> _boneChoices = new();

    /// <summary>A gobo path the texture provider threw on. An exception per row
    /// per frame is a frame-rate cliff, so a failure is remembered.</summary>
    private readonly HashSet<string> _missingGobos = new(StringComparer.Ordinal);

    /// <summary>The attached bone's row label and the snapshot it was derived
    /// from. Re-deriving walks every bone of every actor, so it is done once per
    /// scene revision rather than once per frame.</summary>
    private (BoneId Bone, ulong Revision, string Label)? _attachLabel;

    /// <summary>One bone offered as an attach target: the identity the pick
    /// resolves through, plus the two strings its row shows.</summary>
    private sealed record BoneChoice(
        BoneId Id, string BoneName, string ActorName);

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Light", new[] { ".xivl" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Light", new[] { ".xivl" });
    private readonly global::Poser.UI.Controls.RememberedFolder _folder =
        new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // An imported light is only selectable once the scene refresh has bound
    // it, exactly like a spawned one.
    private readonly global::Poser.UI.Composition.PendingSelection<ILight> _pendingSelect = new();

    /// <summary>The intensity slider's decade notches: where 1 and 10 sit on
    /// the log track, so the tiers read before dragging.</summary>
    private static readonly float[] IntensityMarks = [1f, 10f];

    private static readonly string[] KindOptions =
        ["Directional", "Point", "Spot", "Area"];
    private static readonly string[] FalloffOptions =
        ["Linear", "Quadratic", "Cubic"];

    private readonly global::Poser.UI.Controls.EntityNameModal _names;

    private readonly ScenePane _scenePane;
    private readonly Game.Journal.LightSession _values;

    public LightPane(
        SceneSession scene,
        IEntityBindings bindings,
        ILightingService lighting,
        ISceneLifecycleHistory lifecycle,
        ILightFileService lightFiles,
        ObjectPlacementPreferences placement,
        IPlacementAnchorSource anchors,
        ITransformFacade cleanTransforms,
        IViewportReads viewport,
        ICameraService camera,
        ITextureProvider textures,
        UserNotices notices,
        global::Poser.UI.Controls.EntityNameModal names,
        ScenePane scenePane,
        Game.Journal.LightSession values)
    {
        _values = values;
        _names = names;
        _notices = notices;
        _scene = scene;
        _bindings = bindings;
        _scenePane = scenePane;
        _lighting = lighting;
        _lifecycle = lifecycle;
        _lightFiles = lightFiles;
        _placement = placement;
        _anchors = anchors;
        _cleanTransforms = cleanTransforms;
        // The load dialog carries the ONE choice that changes where the
        // light lands, decided beside the file it applies to.
        _loadBrowser.BottomPanel =
            new FileSidePanel(PlacementBandHeight, DrawPlacementBand);
        _viewport = viewport;
        _camera = camera;
        _textures = textures;
        // The library is embedded and fixed by the time the pane composes,
        // so its count is the walk and its names are the captions.
        _goboGrid = new Crystarium.TexturePicker(
            "light-gobo",
            GoboPreview,
            (uint)lighting.Gobos.Count,
            caption: GoboCaption);
    }

    /// <summary>
    /// Pumped every frame by the window, not by one of the three tab entry
    /// points: the dialogs and the pickers must survive a tab switch, and the
    /// pending import has to resolve while no light is selected — the frame in
    /// which no tab of this pane runs at all.
    /// </summary>
    public void DrawBrowsers()
    {
        _saveBrowser.Draw();
        _loadBrowser.Draw();
        DrawPickers();

        _pendingSelect.Reconcile(
            imported => _bindings.GetLightId(imported) is { } id
                ? SelectionId.ForLight(id)
                : null,
            _scene.Selection);
    }

    /// <summary>The placement band's labels, positional against
    /// <see cref="ObjectPlacementMode"/>.</summary>
    private static readonly string[] PlacementModeLabels =
        ["As saved", "Relative to camera", "Relative to actor"];

    private const float PlacementBandHeight = 56f;

    private void DrawPlacementBand(Vector2 origin, Vector2 size, string? path)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = Crystarium.ActiveTheme.Page.Inset * scale;
        ImGui.SetCursorScreenPos(
            new Vector2(origin.X + inset, origin.Y + inset));
        Crystarium.SegmentedControl(
            "##light-placement-mode",
            PlacementModeLabels,
            (int)_placement.Mode,
            next => _placement.Mode = (ObjectPlacementMode)next,
            itemHelp: index => index switch
            {
                1 => "Keeps the saved offset from the camera",
                2 => "Keeps the saved offset from the selected actor",
                _ => "Exactly where it was saved",
            });
    }

    /// <summary>One placed import: resolves the current anchor the shared
    /// placement mode asks for, refusing by name when it cannot.</summary>
    private ILight? ImportPlaced(
        string path, ObjectPlacementMode mode, out string? refusal)
    {
        if (!_anchors.TryCurrentFor(
                mode, out var position, out var yaw, out refusal))
            return null;
        return _lightFiles.ImportLight(
            path, mode, position, yaw, out refusal);
    }

    /// <summary>Opens the load dialog from outside the pane — the add-entity
    /// menu's "New light from file…".</summary>
    public void OpenLoad()
    {
        _folder.Open(_loadBrowser, path =>
        {
            // The file service owns the spawn, so the add is RECORDED rather
            // than issued here: a light that arrives from a file is still a
            // light the user added, and undo has to know it.
            var imported = _lifecycle.RecordSpawnedLight(
                $"Add light from {System.IO.Path.GetFileNameWithoutExtension(path)}",
                ImportPlaced(path, _placement.Mode, out var refusal));
            if (imported == null)
            {
                _notices.Failed(
                    refusal ?? "Load: the light file could not be read.");
                return;
            }
            _pendingSelect.Arm(imported);
        });
    }

    /// <summary>
    /// The Light tab: what the light IS and what is done with it as a whole —
    /// emission, the mask it projects through, its file, and the two
    /// lifetime actions.
    /// </summary>
    public void DrawLight(Vector2 origin, Vector2 size) =>
        DrawTab("light", origin, size, (page, lightId, light) =>
        {
            // The rule is a divider BETWEEN sections, so the page's first
            // section draws neither the rule nor the margin above it.
            page.Section("General", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, light),
                divider: false);
            page.Section("Light", _openLight, next => _openLight = next,
                form => LightRows(form, light));
            page.Section("Shadows", _openShadows, next => _openShadows = next,
                form => ShadowRows(form, light));
            page.Section("Attach", _openAttach, next => _openAttach = next,
                form => AttachRows(form, light));
            page.Section("File", _openFile, next => _openFile = next,
                form => FileRows(form, light));
            page.Section("Actions", _openActions, next => _openActions = next,
                form => ActionRows(form, lightId, light));
        });

    /// <summary>The two tabs' shared frame: the target lookup and the empty
    /// state. The light's transform is the INSPECTOR RAIL's to edit — the
    /// same TRANSLATION section and rotation gizmo every selection gets.
    /// </summary>
    private void DrawTab(
        string id,
        Vector2 origin,
        Vector2 size,
        Action<Crystarium.PageScope, LightId, ILight> sections)
    {
        Crystarium.Page(id, origin, size, page =>
        {
            var (lightId, light) = TargetLight();
            if (light == null)
            {
                page.EmptyState("Select a light in the sidebar.");
                return;
            }

            sections(page, lightId, light);
        });
    }

    /// <summary>The two retained surfaces, pumped at window level: a popup
    /// opened by a row has to outlive the row's own draw call, and the tab it
    /// was opened from.</summary>
    private void DrawPickers()
    {
        if (_goboGrid.Draw() is { } picked)
            ApplyGoboIndex(picked);
        if (_attachPicker.Draw() is { } bone)
            AttachTo(bone.Item);
    }

    // ── sections ─────────────────────────────────────────────────────────

    private void GeneralRows(Crystarium.FormScope form, ILight light)
    {
        if (!_lighting.IsAvailable)
            form.Status("Lighting is unavailable: game signatures not found.");
        form.Cells(cells =>
        {
            cells.Cell(
                "Enabled",
                cell => cell.Switch("##light-enabled", light.IsOn,
                    value => _values.SetIsOn(light, value)),
                help: "Switch off, settings kept");
            cells.Cell(
                "Reflections",
                cell => cell.Switch("##light-reflections", light.HasReflection,
                    value => _values.SetHasReflection(light, value)),
                help: "Let this light appear in reflective surfaces");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Name",
                cell => cell.TextInput("##light-name", light.Name,
                    value => _values.SetName(light, value)),
                help: "The name this light carries in the sidebar");
            cells.Cell(
                "Type",
                cell => cell.Dropdown("##light-type", KindOptions,
                    (int)light.Kind,
                    selected => _values.SetKind(light, (LightKind)selected)),
                help: "Sun, bulb, cone, or panel");
        });
    }

    private void LightRows(Crystarium.FormScope form, ILight light)
    {
        form.ColorWells("Color", wells =>
        {
            wells.Well("Color", ToDisplayColor(light.Color),
                value => _values.SetColor(light, ToRawColor(value)),
                hdr: true);
        }, help: "HDR color; reaches past white");

        // Intensity carries Ktisis/Brio's full 0–100 native range on log
        // tiers like the environment's light-distance slider — but three
        // decades of curvature instead of the shared two, so the usable 0–1
        // band owns half the travel and the blowout values keep the top.
        form.Cells(cells =>
        {
            cells.Cell(
                "Intensity",
                cell => cell.Slider("##light-intensity", light.Intensity,
                    0f, 100f, value => _values.SetIntensity(light, value),
                    scale: SliderScale.Log,
                    marks: IntensityMarks,
                    logCurvature: 9999f, onBegin: _values.Seal),
                help: "How much light is emitted");
            cells.Cell(
                "Range",
                cell => cell.Slider("##light-range", light.Range, 0f, 999f,
                    value => _values.SetRange(light, value),
                    scale: SliderScale.Log, onBegin: _values.Seal),
                help: "How far the light reaches");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Falloff type",
                cell => cell.Dropdown("##light-falloff-type", FalloffOptions,
                    (int)light.FalloffType,
                    selected => _values.SetFalloffType(light, (LightFalloffType)selected)),
                help: "The dimming curve");
            cells.Cell(
                "Falloff",
                cell => cell.Slider("##light-falloff", light.Falloff,
                    0f, 1000f, value => _values.SetFalloff(light, value),
                    scale: SliderScale.Log, logCurvature: 9999f, onBegin: _values.Seal),
                help: "Dimming toward the cone edge");
        });

        switch (light.Kind)
        {
            case LightKind.Spot:
                form.Cells(cells =>
                {
                    cells.Cell(
                        "Cone angle",
                        cell => cell.Slider("##light-cone", light.SpotAngle,
                            0f, 180f, value => _values.SetSpotAngle(light, value), onBegin: _values.Seal),
                        help: "How wide the cone opens, in degrees");
                    cells.Cell(
                        "Falloff angle",
                        cell => cell.Slider("##light-cone-falloff",
                            light.FalloffAngle, 0f, 180f,
                            value => _values.SetFalloffAngle(light, value), onBegin: _values.Seal),
                        help: "How soft the cone's edge is, in degrees");
                });
                break;
            case LightKind.Area:
                var area = light.AreaAngle;
                form.Cells(cells =>
                {
                    cells.Cell(
                        "Angle X",
                        cell => cell.Slider("##light-area-x", area.X,
                            -90f, 90f,
                            value => _values.SetAreaAngle(
                                light, light.AreaAngle with { X = value }), onBegin: _values.Seal),
                        help: "Skew horizontally, degrees");
                    cells.Cell(
                        "Angle Y",
                        cell => cell.Slider("##light-area-y", area.Y,
                            -90f, 90f,
                            value => _values.SetAreaAngle(
                                light, light.AreaAngle with { Y = value }), onBegin: _values.Seal),
                        help: "Skew vertically, degrees");
                });
                form.Slider("Falloff angle", light.FalloffAngle, 0f, 180f,
                    value => _values.SetFalloffAngle(light, value),
                    help: "How soft the panel's edge is, in degrees", onBegin: _values.Seal);
                break;
        }

        // The gobo is a mask the light projects through, and only the two kinds
        // that project anything can carry one. The service clears it by itself
        // when the kind leaves Spot/Area, so this row only reads that state.
        bool goboSupported = light.Kind is LightKind.Spot or LightKind.Area;
        form.Cells(cells =>
        {
            cells.Cell(
                "Gobo",
                cell => _goboGrid.Field(
                    in cell,
                    GoboIndex(light),
                    next => ApplyGoboIndex(next),
                    disabled: !goboSupported),
                help: goboSupported
                    ? "Project a texture through the light"
                    : "Spot and area lights only.");
            cells.Cell(
                string.Empty,
                cell => cell.Button("##light-gobo-clear", "Clear",
                    () =>
                    {
                        _values.ClearGobo(light);
                    },
                    disabled: light.GoboPath is null),
                help: "Project no mask at all");
        });
    }

    /// <summary>The library index of the applied gobo — or one PAST the
    /// library when none is applied, so the field's tile previews nothing
    /// and stepping lands back inside the catalog.</summary>
    private uint GoboIndex(ILight light)
    {
        if (light.GoboPath is { } path)
        {
            var gobos = _lighting.Gobos;
            for (int i = 0; i < gobos.Count; i++)
            {
                if (string.Equals(
                        gobos[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return (uint)i;
            }
        }
        return (uint)_lighting.Gobos.Count;
    }

    private string GoboCaption(uint index)
    {
        var gobos = _lighting.Gobos;
        return index < gobos.Count ? gobos[(int)index].Name : "None";
    }

    private void ApplyGoboIndex(uint index)
    {
        var (_, light) = TargetLight();
        var gobos = _lighting.Gobos;
        if (light == null || gobos.Count == 0)
            return;
        int clamped = (int)Math.Min(index, (uint)(gobos.Count - 1));
        if (!_values.ApplyGobo(light, gobos[clamped]))
            _notices.Failed("Gobo: the texture could not be applied.");
    }

    /// <summary>
    /// One gobo texture, answered for the current frame. The provider THROWS
    /// for a path the game does not have, so a failure is remembered; a null
    /// wrap with no error is merely "still loading". The WRAP is never
    /// cached: shared textures must be re-resolved each frame.
    /// </summary>
    private TextureProbe GoboPreview(
        uint index, out nint handle, out Vector2 pixels)
    {
        handle = 0;
        pixels = Vector2.Zero;
        var gobos = _lighting.Gobos;
        if (index >= gobos.Count)
            return TextureProbe.Missing;
        string path = gobos[(int)index].Path;
        if (_missingGobos.Contains(path))
            return TextureProbe.Missing;
        Dalamud.Interface.Textures.ISharedImmediateTexture shared;
        try
        {
            shared = _textures.GetFromGame(path);
        }
        catch (Exception)
        {
            _missingGobos.Add(path);
            return TextureProbe.Missing;
        }
        if (!shared.TryGetWrap(out var wrap, out var error))
        {
            if (error is null)
                return TextureProbe.Pending;
            _missingGobos.Add(path);
            return TextureProbe.Missing;
        }
        handle = wrap is null ? 0 : (nint)wrap.Handle.Handle;
        if (wrap is not null)
            pixels = new Vector2(wrap.Width, wrap.Height);
        return handle == 0
            ? TextureProbe.Pending
            : TextureProbe.Ready;
    }

    private void ShadowRows(Crystarium.FormScope form, ILight light)
    {
        form.Cells(cells =>
        {
            cells.Cell(
                "Dynamic",
                cell => cell.Switch("##light-shadow-dynamic",
                    light.CastsDynamicShadows,
                    value => _values.SetCastsDynamicShadows(light, value)),
                help: "Cast shadows that update as the scene moves");
            cells.Cell(
                "Characters",
                cell => cell.Switch("##light-shadow-chara",
                    light.CastsCharacterShadow,
                    value => _values.SetCastsCharacterShadow(light, value)),
                help: "Let characters cast shadows from this light");
            cells.Cell(
                "Objects",
                cell => cell.Switch("##light-shadow-object",
                    light.CastsObjectShadow,
                    value => _values.SetCastsObjectShadow(light, value)),
                help: "Let scenery cast shadows from this light");
        });
        form.Slider("Character range", light.CharacterShadowRange,
            0f, 1000f, value => _values.SetCharacterShadowRange(light, value),
            help: "How far character shadows are still drawn",
            scale: SliderScale.Log, onBegin: _values.Seal);
        form.Cells(cells =>
        {
            cells.Cell(
                "Shadow near",
                cell => cell.Slider("##light-shadow-near",
                    light.ShadowPlaneNear, 0f, 10f,
                    value => _values.SetShadowPlaneNear(light, value),
                    scale: SliderScale.Log, onBegin: _values.Seal),
                help: "The closest distance shadows begin at");
            cells.Cell(
                "Shadow far",
                cell => cell.Slider("##light-shadow-far",
                    light.ShadowPlaneFar, 0f, 1000f,
                    value => _values.SetShadowPlaneFar(light, value),
                    scale: SliderScale.Log, logCurvature: 9999f, onBegin: _values.Seal),
                help: "The furthest distance shadows reach");
        });
    }

    /// <summary>The follow target. Attaching is a per-frame copy of the bone's
    /// position and rotation, so it OWNS the light's transform — the TRANSFORM
    /// section and the in-world gizmo both stand down while it is set.</summary>
    private void AttachRows(Crystarium.FormScope form, ILight light)
    {
        var attached = light.AttachedBone;
        form.Picker(
            "Attach to",
            AttachLabel(attached),
            () => OpenAttachPicker(light),
            actions =>
            {
                actions.Button(
                    "Detach",
                    () =>
                    {
                        _values.SetAttachedBone(light, null);
                        _attachLabel = null;
                    },
                    disabled: attached is null,
                    help: "Stop following");
            },
            help: "Follow a bone");
    }

    /// <summary>"Actor · bone" for the attached bone, memoized on the scene
    /// revision. A bone the snapshot no longer lists still reads as attached —
    /// the service, not this pane, decides when a stale bone detaches.</summary>
    private string AttachLabel(IBone? bone)
    {
        if (bone is null)
            return "None";
        if (_bindings.GetBoneId(bone) is not { } boneId)
            return "Attached";

        ulong revision = _scene.Snapshot.Revision;
        if (_attachLabel is { } cached &&
            cached.Revision == revision &&
            cached.Bone.Equals(boneId))
            return cached.Label;

        foreach (var actor in _scene.Snapshot.Actors)
        {
            foreach (var skeleton in actor.Skeletons)
            {
                foreach (var descriptor in skeleton.Bones)
                {
                    if (!descriptor.Id.Equals(boneId))
                        continue;
                    string label =
                        $"{ActorNames.Display(actor)} · {descriptor.DisplayName}";
                    _attachLabel = (boneId, revision, label);
                    return label;
                }
            }
        }
        return "Attached";
    }

    private void OpenAttachPicker(ILight light)
    {
        _boneChoices.Clear();
        foreach (var actor in _scene.Snapshot.Actors)
        {
            string actorName = ActorNames.Display(actor);
            foreach (var skeleton in actor.Skeletons)
            {
                foreach (var descriptor in skeleton.Bones)
                    _boneChoices.Add(new BoneChoice(
                        descriptor.Id, descriptor.DisplayName, actorName));
            }
        }

        string? selected = light.AttachedBone is { } attached &&
            _bindings.GetBoneId(attached) is { } attachedId
            ? attachedId.ToString()
            : null;
        _attachPicker.Open(
            "attach",
            _boneChoices,
            static choice => choice.BoneName,
            static choice => choice.Id.ToString(),
            selected,
            options: new PickerOptions<BoneChoice>
            {
                Badge = static choice => choice.ActorName,
                // A row carries a bone name and the actor it belongs to; the
                // narrow picker cuts the badge.
                Width = Crystarium.ActiveTheme.Picker.WideWidth,
            });
    }

    private void AttachTo(BoneChoice choice)
    {
        var (_, light) = TargetLight();
        if (light == null)
            return;
        var resolved = _bindings.Resolve(choice.Id);
        if (!resolved.Success || resolved.Value is not { } bone)
        {
            _notices.Failed($"Attach: {resolved.Detail}");
            return;
        }
        _values.SetAttachedBone(light, bone);
        _attachLabel = null;
    }

    /// <summary>Save writes the selected light; load always spawns a new one,
    /// which the pending-select hook makes the selection once the scene has
    /// bound it.</summary>
    private void FileRows(Crystarium.FormScope form, ILight light)
    {
        form.Actions("Light file", actions =>
        {
            actions.Button("Save", () => OpenSave(light),
                help: "Save this light to a file");
            actions.Button("Save to library",
                () => _names.Open(
                    "Save light to library", light.Name,
                    name =>
                    {
                        if (_bindings.GetLightId(light) is { } entryId)
                            _scenePane.SaveLightEntry(
                                entryId.LogicalId, name);
                    }),
                help: "Save into the library");
            actions.Button("Load", OpenLoad,
                help: "Add a light from a file to the scene");
        });
    }

    /// <summary>Public for the sidebar context menu: same dialog, same pump.
    /// </summary>
    public void OpenSave(ILight light)
    {
        _folder.Open(_saveBrowser, path =>
        {
            // The light is frozen at dialog open and can be destroyed while
            // the dialog is up; an invalid handle reads as spawn defaults.
            if (!light.IsValid)
            {
                _notices.Refused("Export: the light no longer exists.");
                return;
            }
            if (_lightFiles.ExportLight(
                    light, path,
                    _anchors.CameraAnchorNow(), _anchors.ActorAnchorNow()))
                _notices.Done($"Light saved to {path}.");
            else
                _notices.Failed(
                    "Export: the light file could not be written.");
        });
    }

    private void ActionRows(
        Crystarium.FormScope form, LightId lightId, ILight light)
    {
        form.Actions("Light", actions =>
        {
            actions.Button("Move to camera",
                () => MoveToCamera(lightId),
                help: "Move to the camera's spot");
            actions.Button("Clone",
                () =>
                {
                    if (_lifecycle.CloneLight(light) == null)
                        _notices.Failed(
                            "Clone: the light could not be created.");
                },
                help: "Duplicate this light");
            // A borrowed native is never destructed: a captured light is given
            // back to the game instead, which is a different promise and reads
            // as a different button.
            if (light.Ownership == LightOwnership.Spawned)
                actions.Button("Destroy",
                    () =>
                    {
                        _lifecycle.DestroyLight(light);
                    },
                    help: "Remove this light from the scene",
                    variant: ButtonVariant.Danger);
            else
                actions.Button("Release",
                    () =>
                    {
                        _lighting.ReleaseLight(light);
                    },
                    help: "Hand it back to the game");
        });

        // Brio's "Destroy All… → Lights → Confirm", armed rather than held:
        // the first press states what is about to go, the second does it.
        int count = _lighting.Lights.Count;
        form.Actions("All lights", actions =>
        {
            actions.Button(
                _destroyAllArmed ? "Confirm destroy all" : "Destroy all",
                () => DestroyAllLights(count),
                disabled: count == 0,
                help: "Destroy spawned, hand back captured",
                variant: ButtonVariant.Danger);
        });
        if (_destroyAllArmed)
            form.Status(
                $"{count} light{(count == 1 ? string.Empty : "s")} will go. ",
                warning: true);
    }

    private void DestroyAllLights(int count)
    {
        if (!_destroyAllArmed)
        {
            _destroyAllArmed = count > 0;
            return;
        }
        _destroyAllArmed = false;
        // Snapshotted first — the sweep mutates the service's own list — and
        // each one goes through the LIFECYCLE seam rather than the service's
        // DestroyAllLights, which is a teardown path with no undo behind it.
        // The seam is also what keeps a captured light a release.
        var doomed = new List<ILight>(_lighting.Lights);
        foreach (var light in doomed)
            _lifecycle.DestroyLight(light);
    }

    /// <summary>Uses the same camera-relative placement as a new light,
    /// recorded as one absolute transform command.</summary>
    private void MoveToCamera(LightId lightId)
    {
        var forward = _camera.GetLookDirection();
        if (forward == Vector3.Zero)
        {
            _notices.Failed("Move to camera: the camera could not be read.");
            return;
        }

        var scale =
            _viewport.GetModelTransform(TransformTargetId.ForLight(lightId))
                is { } current
                ? current.Scale
                : Vector3.One;
        var placement = LightPlacement.FromCamera(_camera.GetCameraPosition(), forward, scale);
        if (!Domain.Transforms.PoseTransform.TryCreate(
                placement.Position,
                placement.Rotation,
                placement.Scale,
                out var target,
                out var invalid))
        {
            _notices.Failed($"Move to camera: {invalid}");
            return;
        }

        var moved = _cleanTransforms.SetAbsolute(
            TransformTargetId.ForLight(lightId), target, "Move light to camera");
        if (!moved.Success)
            _notices.Failed($"Move to camera: {moved.Detail}");
    }

    // ── HDR colour mapping ───────────────────────────────────────────────

    /// <summary>Brio's HDR display mapping: the native value carries far more
    /// than one unit of range, so the well shows its square root over six and
    /// writes the square back.</summary>
    private static Vector4 ToDisplayColor(Vector3 raw) => new(
        MathF.Sqrt(MathF.Max(0f, raw.X) / 6f),
        MathF.Sqrt(MathF.Max(0f, raw.Y) / 6f),
        MathF.Sqrt(MathF.Max(0f, raw.Z) / 6f),
        1f);

    private static Vector3 ToRawColor(Vector4 display) => new(
        display.X * display.X * 6f,
        display.Y * display.Y * 6f,
        display.Z * display.Z * 6f);

    // ── state ────────────────────────────────────────────────────────────

    /// <summary>The selected light and its id, or a null light when the
    /// selection is absent, stale, or already destroyed.</summary>
    private (LightId Id, ILight? Light) TargetLight()
    {
        if (_scene.Selection.Primary is not
            { Kind: SceneEntityKind.Light, Light: { } lightId })
            return (default, null);
        var resolved = _bindings.Resolve(lightId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } light)
            return (lightId, null);
        return (lightId, light);
    }

    // ── transform presentation adapter ──────────────────────────────────

}
