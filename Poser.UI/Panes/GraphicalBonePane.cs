using Poser.Application.Posing;
using Poser.Domain.Scene;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Data;
using Poser.Data.Config;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Inline Body and Face graphical bone selection surface. This pane owns its
/// textures and hit-testing state but has no independent window lifecycle.
/// </summary>
public sealed partial class GraphicalBonePane : IDisposable
{
    private const float HitRadius = 18f;

    private readonly SelectionScope _selection;
    private readonly SelectionScope _targets;
    private readonly SceneSession _scene;

    // Marquee (Anamnesis MouseCanvas): dot positions recorded per frame,
    // drag on empty canvas selects everything inside the rectangle.
    private readonly System.Collections.Generic.List<(SelectionId Id, Vector2 Pos)> _frameDots = new();
    private readonly List<(SelectionId Id, Vector2 Pos, string Name, bool Matches)>
        _dotCandidates = new();
    private Vector2? _marqueeStart;
    private readonly ITextureProvider _textureProvider;
    private readonly ICustomizeReadRuntimePort _customizeRead;

    private readonly GraphicalBoneConfig _config;
    private readonly Dictionary<string, IDalamudTextureWrap?> _textures = new();

    /// <summary>Decodes in flight, polled by <see cref="GetTexture"/>. A map
    /// frame simply skips the image until its decode lands.</summary>
    private readonly Dictionary<string, System.Threading.Tasks.Task<IDalamudTextureWrap>>
        _pendingTextures = new();

    /// <summary>Each map's pixel size, read off the PNG header the moment
    /// its decode is started — so the first frame lays the map out at its
    /// exact size and the decode's arrival shifts nothing.</summary>
    private readonly Dictionary<string, Vector2> _imageSizes = new();

    /// <summary>The size a PNG declares in its header, or null when the
    /// bytes are not a PNG.</summary>
    private static Vector2? PngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50
            || bytes[2] != 0x4E || bytes[3] != 0x47)
            return null;
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0 ? new Vector2(width, height) : null;
    }

    /// <summary>
    /// Mirror selection (Brio GraphicalSidesSwapped): swaps which side each
    /// map dot addresses, so the pose can be edited as seen from the front.
    /// Applies to the graphical maps only — never the tree, matrix, 3D view,
    /// or overlay.
    /// </summary>
    public bool SidesSwapped { get; set; }

    /// <summary>
    /// The map's own bone filter — Brio's <c>BoneSearchControl</c>, which its
    /// graphical window reaches from the same top bar. A map cannot hide a dot
    /// by removing a row, so a dot the filter rejects stays where the drawing
    /// puts it and goes quiet: faint, unhoverable, and outside the marquee.
    /// Held here rather than by the host so both hosts get it, and so it
    /// survives a tab change the way the sidebar's filter does.
    /// </summary>
    private string _filter = "";

    private float _closestHoverDistance;
    private SelectionId? _hoveredBone;
    private int _hoveredDotIndex = -1;
    // Rebuilt per frame from the selected actor's snapshot descriptors: the
    // maps identify dots by (canonical name, partial) without touching the
    // binding registry.
    private readonly Dictionary<BoneId, SelectionId> _dotIds = new();

    private readonly Application.Posing.IIkConfigurationPort _ikPort;
    private readonly IEditorState _editorState;
    private readonly IPoseInteraction _bonePosing;

    private readonly global::Poser.Config.ConfigurationService _configuration;
    private readonly SkeletonOverlayPresentation _presentation;

    public GraphicalBonePane(
        global::Poser.Config.ConfigurationService configuration,
        SceneSession scene,
        PropertiesContext properties,
        ITextureProvider textureProvider,
        ICustomizeReadRuntimePort customizeRead,
        SkeletonOverlayPresentation presentation,
        Application.Posing.IIkConfigurationPort ikPort,
        IEditorState editorState,
        IPoseInteraction bonePosing)
    {
        _configuration = configuration;
        _presentation = presentation;
        _ikPort = ikPort;
        _editorState = editorState;
        _bonePosing = bonePosing;
        _scene = scene;
        _selection = properties.WorkspaceSelection;
        _targets = properties.Selection;
        _textureProvider = textureProvider;
        _customizeRead = customizeRead;

        _config = GraphicalBoneReader.ReadEmbeddedResource();

        // Every map starts decoding now, so the first visit to Body or
        // Face finds its image ready instead of a placeholder that fills
        // a few frames later.
        foreach (var section in _config.PoseImages.Values)
            if (!string.IsNullOrEmpty(section.Image))
                GetTexture(section.Image);
    }

    /// <summary>
    /// Renders the Body (0) or Face (1) map inline inside the AppShell Pose
    /// surface: the seg swaps the pose surface — no window detour.
    /// Returns false when there is nothing to draw (no actor/skeleton).
    /// </summary>
    public bool DrawInline(int page, Vector2 contentArea) => DrawMap(page, contentArea, null);

    private bool DrawMap(int page, Vector2 contentArea, ActorDescriptor? editActor)
    {
        bool editing = editActor != null;
        _drawingEditor = editing;
        object hoverOwner = editing ? _editorHoverOwner : this;
        _presentation.PublishMapHover(hoverOwner, null);
        _drawnPoints.Clear();
        _closestHoverDistance = float.MaxValue;
        _hoveredBone = null;
        _hoveredDotIndex = -1;
        _frameDots.Clear();
        _dotCandidates.Clear();
        _dotKeys.Clear();
        _dotParents.Clear();
        _dotIds.Clear();

        var actor = editActor ?? GetSelectedActor();
        if (actor != null) PopulateBoneIds(actor);
        var actorId = actor?.Id;
        if (actor == null)
            return false;
        var skeleton = actor.CharacterSkeleton;
        if (skeleton == null)
            return false;
        bool humanoid = _customizeRead.IsStandardHumanoid(actor.Id);
        _layout = !humanoid ? null : editing ? _draft?.Points : SelectedPreset(page)?.Points;

        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        // The filter is a FIXED HEADER over the map: it breathes off the
        // surface top and closes with a separator, exactly as the matrix's
        // does — the two surfaces had drifted apart here.
        float bandPad = theme.Page.ActionGap * scale;
        var bandOrigin = ImGui.GetCursorScreenPos()
            + new Vector2(0f, bandPad);
        float bandHeight = bandPad
            + theme.Controls.WorkspaceHeight * scale
            + theme.Page.ActionGap * scale;
        ImGui.SetCursorScreenPos(bandOrigin);
        if (!editing) Crystarium.FilterPill(
            "##graphical-bone-filter",
            _filter,
            next => _filter = next,
            "Search",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Region(MathF.Max(60f, contentArea.X / scale - (humanoid ? 206f : 0f))),
            });
        if (!editing && humanoid) DrawPresetActions(page, actor, bandOrigin, contentArea.X);
        float ruleY = bandOrigin.Y
            + theme.Controls.WorkspaceHeight * scale
            + theme.Page.ActionGap * scale - 1f * scale;
        if (!editing) ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(bandOrigin.X, ruleY),
            new Vector2(bandOrigin.X + contentArea.X, ruleY + 1f * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                theme.FormSeparator)));

        var origin = bandOrigin + new Vector2(
            0f, bandHeight - bandPad + 1f * scale);
        var mapArea = new Vector2(
            contentArea.X, MathF.Max(1f, contentArea.Y - bandHeight));
        if (editing)
        {
            origin = bandOrigin - new Vector2(0f, bandPad);
            mapArea = contentArea;
        }
        if (!humanoid)
        {
            EnsureGeneratedBones(actor);
            ImGui.SetCursorScreenPos(origin);
            Crystarium.ScrollRegion("##generated-bones", mapArea.X / scale, mapArea.Y / scale,
                scope => DrawCanvas(ImGui.GetCursorScreenPos(), new(scope.ContentWidth * scale,
                    MathF.Max(mapArea.Y, (_generatedPoints.Count * 28f + 40f) * scale))));
            return true;
        }
        return DrawCanvas(origin, mapArea);

        bool DrawCanvas(Vector2 origin, Vector2 mapArea)
        {
            _mapOrigin = origin;
            _mapSize = mapArea;
            ImGui.SetCursorScreenPos(origin);
            // The canvas is an ITEM: a press on it belongs to the map — the
            // marquee — never to the window, which used to move instead. The
            // dots and the pages draw over it and take their own hover.
            ImGui.InvisibleButton("##bone-map-canvas", mapArea);
            ImGui.SetItemAllowOverlap();
            ImGui.SetCursorScreenPos(origin);
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(origin, origin + mapArea, true);
            if (!humanoid)
                DrawGeneratedBones(actor, origin, mapArea);
            else if (page == 0)
                DrawBodyPage(skeleton, mapArea);
            else
                DrawFacePage(skeleton, actorId, mapArea);
            DrawAdditionalPoints(actor);
            ResolveAndDrawDots();
            drawList.PopClipRect();

            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
                && ImGui.IsMouseHoveringRect(origin, origin + mapArea);
            if (hovered) _presentation.PublishMapHover(hoverOwner, _hoveredBone?.Bone);

            if (editing)
            {
                HandleEditorCanvas(hovered);
                return true;
            }

            if (_hoveredBone is { } hoveredId && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hovered)
            {
                // Ctrl AND Shift both extend: the map has no
                // row order, so there is no range gesture to reserve Shift for.
                var io = ImGui.GetIO();
                if (io.KeyCtrl || io.KeyShift)
                    _selection.Toggle(hoveredId);
                else
                    _selection.Select(hoveredId);
            }

            // marquee: press on empty canvas + drag = box select (Ctrl adds)
            if (_hoveredBone == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _marqueeStart = ImGui.GetMousePos();

            if (_marqueeStart is { } start)
            {
                var mouse = ImGui.GetMousePos();
                var rmin = Vector2.Min(start, mouse);
                var rmax = Vector2.Max(start, mouse);
                bool isDrag = (rmax - rmin).LengthSquared() > 16f;

                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    if (isDrag)
                    {
                        var fg = ImGui.GetForegroundDrawList();
                        fg.AddRectFilled(
                            rmin,
                            rmax,
                            ImGui.ColorConvertFloat4ToU32(
                                Crystarium.ActiveTheme.Chrome.AccentFill));
                        fg.AddRect(
                            rmin,
                            rmax,
                            ImGui.ColorConvertFloat4ToU32(
                                Crystarium.ActiveTheme.AccentHover));
                    }
                }
                else
                {
                    if (isDrag)
                    {
                        // A marquee that catches nothing is not a selection of
                        // nothing: the selection stands.
                        var caught = new List<SelectionId>();
                        foreach (var (dotId, pos) in _frameDots)
                            if (pos.X >= rmin.X && pos.X <= rmax.X && pos.Y >= rmin.Y && pos.Y <= rmax.Y)
                                caught.Add(dotId);
                        if (caught.Count > 0)
                        {
                            var io = ImGui.GetIO();
                            if (!io.KeyCtrl && !io.KeyShift)
                                _selection.Clear();
                            foreach (var dotId in caught)
                                _selection.Add(dotId);
                        }
                    }
                    _marqueeStart = null;
                }
            }
            return true;
        }
    }

    private void DrawBodyPage(SkeletonDescriptor skeleton, Vector2 contentArea)
    {
        // This is a canvas, not a flow layout. Stable design-space slots keep
        // every image centered and prevent optional tail/toe sections from
        // rearranging the rest of the map as the viewport changes.
        const float designWidth = 2054f;
        const float designHeight = 1147f;
        float s = ImGuiHelpers.GlobalScale;
        float margin = 12f * s;
        var viewportOrigin = ImGui.GetCursorScreenPos();
        var available = Vector2.Max(
            Vector2.One,
            contentArea - new Vector2(margin * 2f));
        float fit = MathF.Min(
            available.X / designWidth,
            available.Y / designHeight);
        var canvasSize = new Vector2(designWidth, designHeight) * fit;
        var canvasOrigin =
            viewportOrigin + (contentArea - canvasSize) * 0.5f;
        _mapOrigin = canvasOrigin;
        _mapSize = Vector2.Max(Vector2.One, canvasSize);

        Vector4 Slot(float x, float y, float width, float height) =>
            new(
                canvasOrigin.X + x * fit,
                canvasOrigin.Y + y * fit,
                width * fit,
                height * fit);

        DrawBoneSectionAt(
            "body",
            Slot(0f, 0f, 674f, 1147f),
            drawMirrors: true,
            skeleton);
        // The ROOT selector: the whole-skeleton anchor gets a dot of its
        // own beneath the figure — no map image ever offered it. It goes
        // through DrawBoneAt, so hover, click, filter and marquee treat
        // it exactly as any drawn dot.
        if (FindBone(skeleton, "n_root") is { } rootBone)
        {
            _pointSection = "body";
            var rootSeat = Slot(337f, 1105f, 0f, 0f);
            DrawBoneAt(rootBone, new Vector2(rootSeat.X, rootSeat.Y));
        }
        DrawBoneSectionAt(
            "armor",
            Slot(714f, 0f, 700f, 1147f),
            drawMirrors: true,
            skeleton);
        DrawBoneSectionAt(
            "hands",
            Slot(1454f, 0f, 600f, 427f),
            drawMirrors: true,
            skeleton);

        if (FindBone(skeleton, "n_sippo_a") != null)
        {
            DrawBoneSectionAt(
                "tail",
                Slot(1529f, 447f, 450f, 464f),
                drawMirrors: false,
                skeleton);
        }

        // Every dot in this section is an IVCS bone, so with the switch off it
        // would draw as a bare image over an empty map.
        if (FindBone(skeleton, "iv_asi_oya_a_l") != null
            && _configuration.Config.Display.ShowNsfwBones)
        {
            DrawBoneSectionAt(
                "ivcs_toes",
                Slot(1454f, 931f, 600f, 216f),
                drawMirrors: true,
                skeleton);
        }
    }

    /// <summary>The last face map's source dimensions: the reservation
    /// aspect while a face decode is in flight, so the image lands inside an
    /// already reserved rect instead of popping the canvas in a frame late.
    /// The config records no image sizes, so before any face map has ever
    /// decoded the reservation is square — every head map is near-square.
    /// </summary>
    private Vector2 _faceSourceSize = Vector2.One;

    private void DrawFacePage(SkeletonDescriptor skeleton, ActorId? actorId, Vector2 contentArea)
    {
        // Face-map variant (race → head section) is a native customize read
        // and lives behind the Game read port; without a stable id for the
        // actor the map keeps the default human section.
        string headSection = actorId is { } id
            ? _customizeRead.HeadSectionFor(id)
            : ICustomizeReadRuntimePort.DefaultHeadSection;
        if (!_config.PoseImages.TryGetValue(headSection, out var section) ||
            string.IsNullOrEmpty(section.Image))
        {
            // No head map resolves for this model — a minion, a mount, a
            // creature. Brio says so where it happens; drawing nothing reads
            // as a broken page (PosingGraphicalWindow.cs:534-543). Brio also
            // offers "Make Human", which is an APPEARANCE action and stays
            // with Glamourer under the standing exclusion.
            DrawFaceEmptyState(
                contentArea,
                "This model has no face map. Face posing here is for "
                    + "humanoid characters; the bone list and the matrix "
                    + "still reach every bone it has.");
            return;
        }
        var texture = GetTexture(section.Image);
        if (texture == null && !_pendingTextures.ContainsKey(section.Image))
        {
            // The section names an image the build cannot decode. That is a
            // packaging fault rather than a property of the actor, and it is
            // worth saying out loud instead of leaving a blank rectangle.
            DrawFaceEmptyState(
                contentArea,
                "The face map for this model could not be loaded.");
            return;
        }

        float s = ImGuiHelpers.GlobalScale;
        float margin = 12f * s;
        var viewportOrigin = ImGui.GetCursorScreenPos();
        var available = Vector2.Max(
            Vector2.One,
            contentArea - new Vector2(margin * 2f));
        var sourceSize = texture != null
            ? new Vector2(texture.Width, texture.Height)
            : _imageSizes.TryGetValue(section.Image, out var declared)
                ? declared
                : _faceSourceSize;
        float fit = MathF.Min(
            available.X / sourceSize.X,
            available.Y / sourceSize.Y);
        var imageSize = sourceSize * fit;
        var imageOrigin =
            viewportOrigin + (contentArea - imageSize) * 0.5f;
        _mapOrigin = imageOrigin;
        _mapSize = Vector2.Max(Vector2.One, imageSize);
        if (texture == null)
        {
            // Reserve the map's rect and paint a quiet fill in it: the
            // decode's arrival must not shift a single pixel of layout.
            DrawPendingFill(imageOrigin, imageSize);
        }
        _faceSourceSize = sourceSize;
        DrawBoneSectionAt(
            headSection,
            new Vector4(
                imageOrigin.X,
                imageOrigin.Y,
                imageSize.X,
                imageSize.Y),
            drawMirrors: true,
            skeleton);
    }

    /// <summary>The map's empty state, centred in the page's own content box.
    /// The maps draw with raw draw-list calls and hold no page scope, so this
    /// reproduces the form's hint tone rather than borrowing
    /// <c>PageScope.EmptyState</c>.</summary>
    private static void DrawFaceEmptyState(Vector2 contentArea, string text)
    {
        float s = ImGuiHelpers.GlobalScale;
        var style = new TextStyle
        {
            Size = Crystarium.ActiveTheme.Typography.LabelSize,
            Color = Crystarium.ActiveTheme.FormHint,
        };
        // Wrapped to a comfortable measure and centred horizontally; the run
        // sits at the page's upper third, where a reader looks first, rather
        // than at a vertical centre that would need the wrapped height.
        float wrap = MathF.Max(1f, MathF.Min(contentArea.X - 32f * s, 360f * s));
        var origin = ImGui.GetCursorScreenPos();
        Crystarium.TextAt(
            origin + new Vector2(
                (contentArea.X - wrap) * 0.5f,
                contentArea.Y * 0.35f),
            text,
            style,
            TextConstraint.Wrap(wrap / s, alignment: TextAlign.Center));
    }

    private void DrawBoneSectionAt(
        string sectionName,
        Vector4 rect,
        bool drawMirrors,
        SkeletonDescriptor skeleton)
    {
        if (!_config.PoseImages.TryGetValue(sectionName, out var section) ||
            string.IsNullOrEmpty(section.Image))
            return;
        var texture = GetTexture(section.Image);
        var min = new Vector2(rect.X, rect.Y);
        var size = new Vector2(rect.Z, rect.W);
        Vector2 sourceSize;
        if (texture != null)
        {
            ImGui.GetWindowDrawList().AddImage(texture.Handle, min, min + size);
            sourceSize = new Vector2(texture.Width, texture.Height);
        }
        else if (_pendingTextures.ContainsKey(section.Image)
            && _imageSizes.TryGetValue(section.Image, out var declared))
        {
            // The decode is in flight but the header already said the size,
            // so the dots take their final places now over a quiet fill;
            // the image lands under them and nothing moves.
            DrawPendingFill(min, size);
            sourceSize = declared;
        }
        else
        {
            // A missing or failed image stays absent; a decode whose header
            // gave no size reserves its rect and waits.
            if (_pendingTextures.ContainsKey(section.Image))
                DrawPendingFill(min, size);
            return;
        }
        var scalingFactors = size / sourceSize;

        _pointSection = sectionName.EndsWith("head", StringComparison.Ordinal)
            || sectionName.StartsWith("viera_head", StringComparison.Ordinal) ? "face" : sectionName;
        foreach (var graphicBone in section.Bones)
        {
            var bone = FindBone(skeleton, graphicBone.Name);
            var mirrorBoneName = drawMirrors ? GetMirrorBoneName(graphicBone.Name) : null;
            var mirrorBone = mirrorBoneName != null ? FindBone(skeleton, mirrorBoneName) : null;

            var primaryPosition = min + new Vector2(
                graphicBone.PositionVector.X * scalingFactors.X,
                graphicBone.PositionVector.Y * scalingFactors.Y);
            if (bone != null)
                DrawBoneAt(bone, primaryPosition);

            if (mirrorBone != null)
            {
                float mirrorX = sourceSize.X - graphicBone.PositionVector.X;
                var mirrorPosition = min + new Vector2(
                    mirrorX * scalingFactors.X,
                    graphicBone.PositionVector.Y * scalingFactors.Y);
                DrawBoneAt(mirrorBone, mirrorPosition);
            }
        }
    }

    private void DrawBoneAt(BoneDescriptor bone, Vector2 screenPos)
    {
        // Selection identity is the stable id from the snapshot table; the
        // descriptor also supplies labels and parent connections without a native read.
        if (!_dotIds.ContainsKey(bone.Id)) return;
        var portable = Poser.Domain.Posing.PortableBoneId.From(bone.Id);
        string section = _pointSection;
        if (_layout != null && !_addingCustomPoint)
        {
            var saved = _layout.FirstOrDefault(item => item.Bone == portable && item.Section == section);
            if (saved == null) return;
            screenPos = _mapOrigin + new Vector2(saved.X, saved.Y) * _mapSize;
        }
        _drawnPoints.Add((portable, section));
        // Layout identity stays unswapped; Mirror changes the posing target, never saved positions.
        if (!_drawingEditor && SidesSwapped && GetMirrorBoneName(bone.Id.CanonicalName) is { } mirrorName)
        {
            if (_scene.Snapshot.FindActor(bone.Id.Skeleton.Actor) is { } actor
                && AvailableBones(actor).TryGetValue(portable with { CanonicalName = mirrorName }, out var mirrored)
                && _dotIds.ContainsKey(mirrored.Id))
                bone = mirrored;
        }
        var selectionId = _dotIds[bone.Id];
        bool matches = _drawingEditor || MatchesFilter(bone.DisplayName, bone.Id.CanonicalName);
        // Brio's line rule, copied whole: a connector goes to the DIRECT
        // parent only, and only when that parent has a dot on the SAME
        // panel — an ancestor walk wired panels together into insanity
        // (2026-09-01).
        (string, PoseSlot, string, int)? parentKey = bone.Parent is { } parent
            ? (_pointSection, parent.Slot, parent.CanonicalName, parent.PartialId)
            : null;
        _dotKeys[(_pointSection, bone.Id.Slot, bone.Id.CanonicalName, bone.Id.PartialId)] =
            screenPos;
        _dotParents.Add(parentKey);
        _dotCandidates.Add((selectionId, screenPos, bone.DisplayName, matches));
        // A filtered-out dot is outside the marquee too: dragging a box over
        // the map must select what the map is offering, not what it is
        // greying.
        if (matches)
            _frameDots.Add((selectionId, screenPos));
    }

    /// <summary>Brio's <c>BoneSearchControl</c> matcher: the friendly name or
    /// the raw skeleton name, case-insensitively, and an empty filter matches
    /// everything.</summary>
    private bool MatchesFilter(string displayName, string canonicalName) =>
        _filter.Length == 0
        || displayName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || canonicalName.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>How present a dot the filter rejected stays: enough to keep
    /// the map readable as a map, not enough to be mistaken for an
    /// offer.</summary>
    private const float FilteredDotOpacity = 0.25f;

    private static uint FadeU32(uint color, float factor)
    {
        uint alpha = (uint)Math.Clamp(
            ((color >> 24) & 0xFF) * factor, 0f, 255f);
        return (color & 0x00FFFFFF) | (alpha << 24);
    }

    private readonly Dictionary<(string, PoseSlot, string, int), Vector2> _dotKeys =
        new();
    private readonly List<(string, PoseSlot, string, int)?> _dotParents = new();

    private void ResolveAndDrawDots()
    {
        float s = ImGuiHelpers.GlobalScale;
        var mouse = ImGui.GetMousePos();
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
            if (!candidate.Matches || !ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
                || !ImGui.IsMouseHoveringRect(candidate.Pos - new Vector2(HitRadius * s), candidate.Pos + new Vector2(HitRadius * s)))
                continue;
            float distance = Vector2.Distance(mouse, candidate.Pos);
            if (distance < HitRadius * s && distance < _closestHoverDistance)
            {
                _closestHoverDistance = distance;
                _hoveredBone = candidate.Id;
                _hoveredDotIndex = i;
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        string? hoveredName = null;
        // The overlay's own facts, on the map (#98 bullet 3): armed IK
        // chains wear the IK color and the selection's opposite-side
        // partners wear the mirror color — same swatches, same priority
        // (selected > hovered > IK > mirror), body and face alike.
        var skeletonColors =
            _configuration.Config.Skeleton;
        var armedIk = new HashSet<(SkeletonId Skeleton, int Partial, string Name)>();
        var checkedSkeletons = new HashSet<SkeletonId>();
        var mirrorPartners = new HashSet<(SkeletonId Skeleton, int Partial, string Name)>();
        foreach (var (id, _, _, _) in _dotCandidates)
        {
            if (id.Bone is not { } fact)
                continue;
            if (checkedSkeletons.Add(fact.Skeleton))
            {
                foreach (var chain in _ikPort.Chains(fact.Skeleton))
                {
                    if (!chain.Config.Enabled)
                        continue;
                    foreach (var chainBone in chain.Bones)
                        armedIk.Add((fact.Skeleton, chain.Endpoint.PartialId, chainBone));
                }
            }
            if (!_selection.IsSelected(id))
                continue;
            // Link (Copy) and Mirror both drive the opposite-side
            // partner, so BOTH modes show it — resolved per bone through
            // the one symmetry rule, in the maps exactly as the overlay.
            var appConfig =
                _configuration.Config;
            if (Core.BoneSymmetry.EffectiveMode(
                    appConfig.PerBoneSymmetry,
                    appConfig.BoneSymmetryOverrides,
                    appConfig.AutoLinkPairedBones,
                    _editorState.SymmetryMode,
                    fact.CanonicalName) != SymmetryMode.Off
                && Core.PoseMath.GetMirrorBoneName(fact.CanonicalName)
                    is { } mirror)
                mirrorPartners.Add((fact.Skeleton, fact.PartialId, mirror));
            if (_bonePosing.LinkedBonesEnabled)
                foreach (var linked in global::Poser.Domain.Posing
                    .BoneLinkCatalog.GetLinked(fact.CanonicalName))
                    mirrorPartners.Add((fact.Skeleton, fact.PartialId, linked));
        }
        float circleRadius = skeletonColors.MapDotRadius;
        // Colors resolve FIRST so the connector lines can wear the child
        // dot's own color and draw UNDER every circle.
        Span<uint> colors = _dotCandidates.Count <= 512
            ? stackalloc uint[_dotCandidates.Count]
            : new uint[_dotCandidates.Count];
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
            bool isSelected = _selection.IsSelected(candidate.Id);
            bool isHovered = i == _hoveredDotIndex;
            var fact = candidate.Id.Bone;
            // Selection is the THEME's primary, not ImGui's style checkmark.
            uint circleColor = isSelected
                ? skeletonColors.ResolveSelectedBoneColor()
                : isHovered
                    ? skeletonColors.ResolveHoveredBoneColor()
                    : fact is { } ikBone && armedIk.Contains((ikBone.Skeleton, ikBone.PartialId, ikBone.CanonicalName))
                        ? skeletonColors.IkChainColor
                        : fact is { } paired && mirrorPartners.Contains((paired.Skeleton, paired.PartialId, paired.CanonicalName))
                            ? skeletonColors.MirroredBoneColor
                            : skeletonColors.BoneColor;
            // A dot the filter rejects keeps its place — the map is a
            // drawing and its dots ARE the anatomy — and goes faint, which
            // is a map's way of saying what a list says by not listing a row.
            if (!candidate.Matches)
                circleColor = FadeU32(circleColor, FilteredDotOpacity);
            colors[i] = circleColor;
        }
        // The connector lines the maps were missing (#98): each dot to its
        // direct on-map parent, in the child's own color, faded so the
        // anatomy stays a drawing, under every circle.
        for (int i = 0; i < _dotCandidates.Count
            && i < _dotParents.Count; i++)
        {
            if (_dotParents[i] is not { } parentKey
                || !_dotKeys.TryGetValue(parentKey, out var parentPos))
                continue;
            drawList.AddLine(
                _dotCandidates[i].Pos, parentPos,
                FadeU32(colors[i], 0.55f), 1f * s);
        }
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
            bool isSelected = _selection.IsSelected(candidate.Id);
            bool isHovered = i == _hoveredDotIndex;
            uint circleColor = colors[i];
            drawList.AddCircleFilled(
                candidate.Pos, circleRadius * s,
                ImGui.GetColorU32(ImGuiCol.ChildBg));
            drawList.AddCircle(
                candidate.Pos, circleRadius * s, circleColor);
            if (isSelected || isHovered)
            {
                drawList.AddCircleFilled(
                    candidate.Pos,
                    (circleRadius - 3f) * s,
                    circleColor);
            }
            if (isHovered)
                hoveredName = candidate.Name;
        }

        if (hoveredName != null
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            Crystarium.HoverHelp.Preview("gbp-dot",
                mouse - new Vector2(4f, 4f),
                mouse + new Vector2(4f, 4f),
                hoveredName);
        }
    }

    /// <summary>The reserved rect's fill while its decode is in flight: the
    /// theme's raised surface, so a pending map reads as a surface rather
    /// than a hole, and its arrival changes pixels but never layout.</summary>
    private static void DrawPendingFill(Vector2 min, Vector2 size)
    {
        ImGui.GetWindowDrawList().AddRectFilled(
            min,
            min + size,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                Crystarium.ActiveTheme.SurfaceRaised)),
            Crystarium.ActiveTheme.Radii.Surface * ImGuiHelpers.GlobalScale);
    }

    private IDalamudTextureWrap? GetTexture(string imageName)
    {
        if (_textures.TryGetValue(imageName, out var cached))
            return cached;

        // The decode is ASYNC and the map draws nothing until it lands: the
        // old task.Wait() here blocked the render thread for the whole PNG
        // decode, which Dalamud logged as a 300ms-class UiBuilder hitch on
        // every first draw of an uncached variant (fresh load, redraws that
        // switch race/gender maps).
        if (_pendingTextures.TryGetValue(imageName, out var pending))
        {
            if (!pending.IsCompleted)
                return null;
            _pendingTextures.Remove(imageName);
            try
            {
                _textures[imageName] = pending.Result;
            }
            catch
            {
                _textures[imageName] = null;
            }
            return _textures[imageName];
        }

        var bytes = GraphicalBoneReader.GetImageBytes(imageName);
        if (bytes == null)
        {
            _textures[imageName] = null;
            return null;
        }

        if (PngSize(bytes) is { } declared)
            _imageSizes[imageName] = declared;
        _pendingTextures[imageName] = _textureProvider.CreateFromImageAsync(bytes);
        return null;
    }

    private static BoneDescriptor? FindBone(SkeletonDescriptor skeleton, string name) =>
        skeleton.Bones.FirstOrDefault(bone => bone.Id.CanonicalName == name);

    private ActorDescriptor? GetSelectedActor()
    {
        if (_targets.PrimaryActor is not { } id)
            return _scene.Snapshot.Actors.FirstOrDefault();
        return _scene.Snapshot.FindActor(id);
    }

    private void PopulateBoneIds(ActorDescriptor actor)
    {
        foreach (var bone in AvailableBones(actor).Values)
        {
            bool showNsfw = _configuration.Config.Display.ShowNsfwBones;
            if (!showNsfw && Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName))
                continue;
            _dotIds[bone.Id] = SelectionId.ForBone(bone.Id);
        }
    }

    private static string? GetMirrorBoneName(string boneName)
    {
        if (boneName.EndsWith("_l"))
            return boneName[..^2] + "_r";
        if (boneName.EndsWith("_r"))
            return boneName[..^2] + "_l";
        return null;
    }

    public void Dispose()
    {
        _presentation.PublishMapHover(this, null);
        _presentation.PublishMapHover(_editorHoverOwner, null);
        foreach (var texture in _textures.Values)
        {
            texture?.Dispose();
        }
        _textures.Clear();
        // In-flight decodes dispose their wrap on arrival instead of leaking.
        foreach (var pending in _pendingTextures.Values)
            pending.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully)
                        task.Result.Dispose();
                },
                System.Threading.Tasks.TaskScheduler.Default);
        _pendingTextures.Clear();
    }
}
