using System;
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
using Poser.Game.Bindings;
using Poser.Game.Transforms;
using Poser.Services;
using DomainDelta = Poser.Domain.Transforms.TransformDelta;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainPivot = Poser.Domain.Transforms.PivotMode;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;
using GestureId = Poser.Application.Transforms.TransformGestureId;

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
    private readonly StableBindingRegistry _bindings;
    private readonly ILightingService _lighting;
    private readonly ILightFileService _lightFiles;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly ICameraService _camera;
    private readonly ITextureProvider _textures;

    private string _status = string.Empty;
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
        new("Save Light", new[] { ".poserlight" }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Light", new[] { ".poserlight" });
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // An imported light is only selectable once the scene refresh has bound
    // it, exactly like a spawned one.
    private ILight? _pendingSelect;

    /// <summary>The intensity slider's decade notches: where 1 and 10 sit on
    /// the log track, so the tiers read before dragging.</summary>
    private static readonly float[] IntensityMarks = [1f, 10f];

    private static readonly string[] KindOptions =
        ["Directional", "Point", "Spot", "Area"];
    private static readonly string[] FalloffOptions =
        ["Linear", "Quadratic", "Cubic"];

    public LightPane(
        SceneSession scene,
        StableBindingRegistry bindings,
        ILightingService lighting,
        ILightFileService lightFiles,
        CleanTransformFacade cleanTransforms,
        Game.Viewport.ViewportProjection viewport,
        ICameraService camera,
        ITextureProvider textures)
    {
        _scene = scene;
        _bindings = bindings;
        _lighting = lighting;
        _lightFiles = lightFiles;
        _cleanTransforms = cleanTransforms;
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

        if (_pendingSelect is { } imported &&
            _bindings.GetLightId(imported) is { } lightId)
        {
            _scene.Selection.Select(SelectionId.ForLight(lightId));
            _pendingSelect = null;
        }
    }

    /// <summary>Opens the load dialog from outside the pane — the add-entity
    /// menu's "New light from file…".</summary>
    public void OpenLoad()
    {
        _loadBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            var imported = _lightFiles.ImportLight(path);
            if (imported == null)
            {
                _status = "Load: the light file could not be read.";
                return;
            }
            _pendingSelect = imported;
            _status = string.Empty;
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
            page.Section("GENERAL", _openGeneral, next => _openGeneral = next,
                form => GeneralRows(form, light),
                divider: false);
            page.Section("LIGHT", _openLight, next => _openLight = next,
                form => LightRows(form, light));
            page.Section("ATTACH", _openAttach, next => _openAttach = next,
                form => AttachRows(form, light));
            page.Section("FILE", _openFile, next => _openFile = next,
                form => FileRows(form, light));
            page.Section("ACTIONS", _openActions, next => _openActions = next,
                form => ActionRows(form, lightId, light));
        });

    /// <summary>The Shadows tab: everything the light casts, and nothing
    /// else.</summary>
    public void DrawShadows(Vector2 origin, Vector2 size) =>
        DrawTab("light-shadows", origin, size, (page, _, light) =>
        {
            page.Section("SHADOWS", _openShadows, next => _openShadows = next,
                form => ShadowRows(form, light),
                divider: false);
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

            page.Status(_status);
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
                    value => light.IsOn = value),
                help: "Turn the light off without losing any of its settings");
            cells.Cell(
                "Reflections",
                cell => cell.Switch("##light-reflections", light.HasReflection,
                    value => light.HasReflection = value),
                help: "Let this light appear in reflective surfaces");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Name",
                cell => cell.TextInput("##light-name", light.Name,
                    value => light.Name = value),
                help: "The name this light carries in the sidebar");
            cells.Cell(
                "Type",
                cell => cell.Dropdown("##light-type", KindOptions,
                    (int)light.Kind,
                    selected => light.Kind = (LightKind)selected),
                help: "How the light emits: a sun, a bulb, a cone, or a panel");
        });
    }

    private void LightRows(Crystarium.FormScope form, ILight light)
    {
        form.ColorWells("Color", wells =>
        {
            wells.Well("Color", ToDisplayColor(light.Color),
                value => light.Color = ToRawColor(value),
                hdr: true);
        }, help: "The light's color; the native value is HDR and reaches past white");

        // Intensity carries Ktisis/Brio's full 0–100 native range on log
        // tiers like the environment's light-distance slider — but three
        // decades of curvature instead of the shared two, so the usable 0–1
        // band owns half the travel and the blowout values keep the top.
        form.Cells(cells =>
        {
            cells.Cell(
                "Intensity",
                cell => cell.Slider("##light-intensity", light.Intensity,
                    0f, 100f, value => light.Intensity = value,
                    scale: SliderScale.Log,
                    marks: IntensityMarks,
                    logCurvature: 9999f),
                help: "How much light is emitted");
            cells.Cell(
                "Range",
                cell => cell.Slider("##light-range", light.Range, 0f, 999f,
                    value => light.Range = value, format: "0",
                    scale: SliderScale.Log),
                help: "How far the light reaches");
        });
        form.Cells(cells =>
        {
            cells.Cell(
                "Falloff type",
                cell => cell.Dropdown("##light-falloff-type", FalloffOptions,
                    (int)light.FalloffType,
                    selected => light.FalloffType = (LightFalloffType)selected),
                help: "The curve the light dims along over its range");
            cells.Cell(
                "Falloff",
                cell => cell.Slider("##light-falloff", light.Falloff,
                    0f, 1000f, value => light.Falloff = value,
                    scale: SliderScale.Log, logCurvature: 9999f),
                help: "How sharply the light dims toward the edge of its "
                    + "range");
        });

        switch (light.Kind)
        {
            case LightKind.Spot:
                form.Cells(cells =>
                {
                    cells.Cell(
                        "Cone angle",
                        cell => cell.Slider("##light-cone", light.SpotAngle,
                            0f, 180f, value => light.SpotAngle = value,
                            format: "0"),
                        help: "How wide the cone opens, in degrees");
                    cells.Cell(
                        "Falloff angle",
                        cell => cell.Slider("##light-cone-falloff",
                            light.FalloffAngle, 0f, 180f,
                            value => light.FalloffAngle = value, format: "0"),
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
                            value => light.AreaAngle =
                                light.AreaAngle with { X = value },
                            format: "0"),
                        help: "How far the panel skews horizontally, in "
                            + "degrees");
                    cells.Cell(
                        "Angle Y",
                        cell => cell.Slider("##light-area-y", area.Y,
                            -90f, 90f,
                            value => light.AreaAngle =
                                light.AreaAngle with { Y = value },
                            format: "0"),
                        help: "How far the panel skews vertically, in "
                            + "degrees");
                });
                form.Slider("Falloff angle", light.FalloffAngle, 0f, 180f,
                    value => light.FalloffAngle = value, "0",
                    help: "How soft the panel's edge is, in degrees");
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
                    ? "Project a texture through the light, like a window's "
                        + "shadow"
                    : "Spot and area lights only.");
            cells.Cell(
                string.Empty,
                cell => cell.Button("##light-gobo-clear", "Clear",
                    () =>
                    {
                        _lighting.ClearGobo(light);
                        _status = string.Empty;
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
        _status = _lighting.ApplyGobo(light, gobos[clamped])
            ? string.Empty
            : "Gobo: the texture could not be applied.";
    }

    /// <summary>
    /// One gobo texture, answered for the current frame. The provider THROWS
    /// for a path the game does not have, so a failure is remembered; a null
    /// wrap with no error is merely "still loading". The WRAP is never
    /// cached: shared textures must be re-resolved each frame.
    /// </summary>
    private TextureProbe GoboPreview(uint index, out nint handle)
    {
        handle = 0;
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
                    value => light.CastsDynamicShadows = value),
                help: "Cast shadows that update as the scene moves");
            cells.Cell(
                "Characters",
                cell => cell.Switch("##light-shadow-chara",
                    light.CastsCharacterShadow,
                    value => light.CastsCharacterShadow = value),
                help: "Let characters cast shadows from this light");
            cells.Cell(
                "Objects",
                cell => cell.Switch("##light-shadow-object",
                    light.CastsObjectShadow,
                    value => light.CastsObjectShadow = value),
                help: "Let scenery cast shadows from this light");
        });
        form.Slider("Character range", light.CharacterShadowRange,
            0f, 1000f, value => light.CharacterShadowRange = value, "0",
            help: "How far character shadows are still drawn",
            scale: SliderScale.Log);
        form.Cells(cells =>
        {
            cells.Cell(
                "Shadow near",
                cell => cell.Slider("##light-shadow-near",
                    light.ShadowPlaneNear, 0f, 10f,
                    value => light.ShadowPlaneNear = value,
                    scale: SliderScale.Log),
                help: "The closest distance shadows begin at");
            cells.Cell(
                "Shadow far",
                cell => cell.Slider("##light-shadow-far",
                    light.ShadowPlaneFar, 0f, 1000f,
                    value => light.ShadowPlaneFar = value, format: "0.0",
                    scale: SliderScale.Log, logCurvature: 9999f),
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
                        light.AttachedBone = null;
                        _attachLabel = null;
                        _status = string.Empty;
                    },
                    disabled: attached is null,
                    help: "Leave the light where it is and stop following");
            },
            help: "Make the light follow a bone, one transform copy per frame");
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
                        $"{ActorName(actor)} · {descriptor.DisplayName}";
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
            string actorName = ActorName(actor);
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
            _status = $"Attach: {resolved.Detail}";
            return;
        }
        light.AttachedBone = bone;
        _attachLabel = null;
        _status = string.Empty;
    }

    /// <summary>Nickname / anonymous-mask aware, like every other surface.
    /// </summary>
    private static string ActorName(ActorDescriptor actor) =>
        ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, actor.Name);

    /// <summary>Save writes the selected light; load always spawns a new one,
    /// which the pending-select hook makes the selection once the scene has
    /// bound it.</summary>
    private void FileRows(Crystarium.FormScope form, ILight light)
    {
        form.Actions("Light file", actions =>
        {
            actions.Button("Save", () => OpenSave(light),
                help: "Write this light and all of its settings to a file");
            actions.Button("Load", OpenLoad,
                help: "Add a light from a file to the scene");
        });
    }

    private void OpenSave(ILight light)
    {
        _saveBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            // The light is frozen at dialog open and can be destroyed while
            // the dialog is up; an invalid handle reads as spawn defaults.
            if (!light.IsValid)
            {
                _status = "Export: the light no longer exists.";
                return;
            }
            bool exported = _lightFiles.ExportLight(light, path);
            _status = exported
                ? string.Empty
                : "Export: the light file could not be written.";
        });
    }

    private void ActionRows(
        Crystarium.FormScope form, LightId lightId, ILight light)
    {
        form.Actions("Placement", actions =>
        {
            actions.Button("Move to camera",
                () => MoveToCamera(lightId),
                help: "Put the light where the camera is, facing the same way");
        });
        form.Actions("Light", actions =>
        {
            actions.Button("Clone",
                () =>
                {
                    var clone = _lighting.CloneLight(light);
                    _status = clone == null
                        ? "Clone: the light could not be created."
                        : string.Empty;
                },
                help: "Create a second light with every setting of this one");
            // A borrowed native is never destructed: a captured light is given
            // back to the game instead, which is a different promise and reads
            // as a different button.
            if (light.Ownership == LightOwnership.Spawned)
                actions.Button("Destroy",
                    () =>
                    {
                        _lighting.DestroyLight(light);
                        _status = string.Empty;
                    },
                    help: "Remove this light from the scene",
                    variant: ButtonVariant.Danger);
            else
                actions.Button("Release",
                    () =>
                    {
                        _lighting.ReleaseLight(light);
                        _status = string.Empty;
                    },
                    help: "Give this light back to the game and stop editing it");
        });
    }

    /// <summary>Brio's "move to camera": the light takes the camera's world
    /// position and look direction, written as one absolute command so it
    /// joins undo history like any other transform.</summary>
    private void MoveToCamera(LightId lightId)
    {
        var forward = _camera.GetLookDirection();
        if (forward == Vector3.Zero)
        {
            _status = "Move to camera: the camera could not be read.";
            return;
        }

        var scale =
            _viewport.GetModelTransform(TransformTargetId.ForLight(lightId))
                is { } current
                ? current.Scale
                : Vector3.One;
        // Land ahead of the eye, never AT it: a pivot on the camera
        // degenerates the gizmo projection and WorldToScreen, so the light
        // would arrive handleless and ungrabbable. The rotation aligns the
        // beam axis (+Z) onto the look ray, matching what the overlay draws.
        if (!Domain.Transforms.PoseTransform.TryCreate(
                _camera.GetCameraPosition() + forward * 3f,
                PoseMath.AlignZTo(forward),
                scale,
                out var target,
                out var invalid))
        {
            _status = $"Move to camera: {invalid}";
            return;
        }

        var moved = _cleanTransforms.SetAbsolute(
            TransformTargetId.ForLight(lightId), target, "Move light to camera");
        _status = moved.Success
            ? string.Empty
            : $"Move to camera: {moved.Detail}";
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

    private static Transform ToLegacy(Domain.Transforms.PoseTransform value) =>
        new()
        {
            Position = value.Position,
            Rotation = value.Rotation,
            Scale = value.Scale,
        };
}
