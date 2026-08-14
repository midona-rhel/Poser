using System;
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
using Poser.Game.Bindings;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Inline Body and Face graphical bone selection surface. This pane owns its
/// textures and hit-testing state but has no independent window lifecycle.
/// </summary>
public sealed class GraphicalBonePane : IDisposable
{
    private const float CircleRadius = 6f;
    private const float HitRadius = 18f;

    private readonly SelectionSession _selection;
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;

    // Marquee (Anamnesis MouseCanvas): dot positions recorded per frame,
    // drag on empty canvas selects everything inside the rectangle.
    private readonly System.Collections.Generic.List<(SelectionId Id, Vector2 Pos)> _frameDots = new();
    private readonly List<(SelectionId Id, Vector2 Pos, string Name, bool Matches)>
        _dotCandidates = new();
    private Vector2? _marqueeStart;
    private readonly IActorManager _actorManager;
    private readonly ISkeletonService _skeletonService;
    private readonly ITextureProvider _textureProvider;
    private readonly ICustomizeReadRuntimePort _customizeRead;

    private readonly GraphicalBoneConfig _config;
    private readonly Dictionary<string, IDalamudTextureWrap?> _textures = new();

    /// <summary>Decodes in flight, polled by <see cref="GetTexture"/>. A map
    /// frame simply skips the image until its decode lands.</summary>
    private readonly Dictionary<string, System.Threading.Tasks.Task<IDalamudTextureWrap>>
        _pendingTextures = new();

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
    private readonly Dictionary<(string Canonical, int PartialId), SelectionId> _dotIds = new();

    public GraphicalBonePane(
        SceneSession scene,
        StableBindingRegistry bindings,
        IActorManager actorManager,
        ISkeletonService skeletonService,
        ITextureProvider textureProvider,
        ICustomizeReadRuntimePort customizeRead)
    {
        _scene = scene;
        _selection = scene.Selection;
        _bindings = bindings;
        _actorManager = actorManager;
        _skeletonService = skeletonService;
        _textureProvider = textureProvider;
        _customizeRead = customizeRead;

        _config = GraphicalBoneReader.ReadEmbeddedResource();

    }

    /// <summary>
    /// Renders the Body (0) or Face (1) map inline inside the AppShell Pose
    /// surface: the seg swaps the pose surface — no window detour.
    /// Returns false when there is nothing to draw (no actor/skeleton).
    /// </summary>
    public bool DrawInline(int page, Vector2 contentArea)
    {
        _closestHoverDistance = float.MaxValue;
        _hoveredBone = null;
        _hoveredDotIndex = -1;
        _frameDots.Clear();
        _dotCandidates.Clear();
        _dotIds.Clear();

        var (actor, actorId) = GetSelectedActor();
        if (actor == null)
            return false;
        var skeleton = _skeletonService.GetSkeleton(actor);
        if (skeleton == null)
            return false;

        var bandOrigin = ImGui.GetCursorScreenPos();
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float bandHeight =
            theme.Controls.WorkspaceHeight * scale + theme.Page.ActionGap * scale;
        ImGui.SetCursorScreenPos(bandOrigin);
        Crystarium.FilterPill(
            "##graphical-bone-filter",
            _filter,
            next => _filter = next,
            "Filter bones",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Region(MathF.Min(
                    theme.Matrix.FilterWidth, contentArea.X / scale)),
            });

        var origin = bandOrigin + new Vector2(0f, bandHeight);
        var mapArea = new Vector2(
            contentArea.X, MathF.Max(1f, contentArea.Y - bandHeight));
        ImGui.SetCursorScreenPos(origin);

        if (page == 0)
            DrawBodyPage(skeleton, mapArea);
        else
            DrawFacePage(skeleton, actorId, mapArea);
        ResolveAndDrawDots();

        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
            && ImGui.IsMouseHoveringRect(origin, origin + mapArea);

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
                    var io = ImGui.GetIO();
                    if (!io.KeyCtrl && !io.KeyShift)
                        _selection.Clear();
                    foreach (var (dotId, pos) in _frameDots)
                    {
                        if (pos.X >= rmin.X && pos.X <= rmax.X && pos.Y >= rmin.Y && pos.Y <= rmax.Y)
                            _selection.Add(dotId);
                    }
                }
                _marqueeStart = null;
            }
        }
        return true;
    }

    private void DrawBodyPage(ISkeleton skeleton, Vector2 contentArea)
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

        if (skeleton.GetBone("n_sippo_a") != null)
        {
            DrawBoneSectionAt(
                "tail",
                Slot(1529f, 447f, 450f, 464f),
                drawMirrors: false,
                skeleton);
        }

        // Every dot in this section is an IVCS bone, so with the switch off it
        // would draw as a bare image over an empty map.
        if (skeleton.GetBone("iv_asi_oya_a_l") != null
            && Config.ConfigurationService.Instance.Config.Display.ShowNsfwBones)
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

    private void DrawFacePage(ISkeleton skeleton, ActorId? actorId, Vector2 contentArea)
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
            : _faceSourceSize;
        float fit = MathF.Min(
            available.X / sourceSize.X,
            available.Y / sourceSize.Y);
        var imageSize = sourceSize * fit;
        var imageOrigin =
            viewportOrigin + (contentArea - imageSize) * 0.5f;
        if (texture == null)
        {
            // Reserve the map's rect and paint a quiet fill in it: the
            // decode's arrival must not shift a single pixel of layout.
            DrawPendingFill(imageOrigin, imageSize);
            return;
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
        ISkeleton skeleton)
    {
        if (!_config.PoseImages.TryGetValue(sectionName, out var section) ||
            string.IsNullOrEmpty(section.Image))
            return;
        var texture = GetTexture(section.Image);
        var min = new Vector2(rect.X, rect.Y);
        var size = new Vector2(rect.Z, rect.W);
        if (texture == null)
        {
            // The slot's rect is computable without the texture, so an
            // in-flight decode reserves it with a quiet fill instead of
            // dropping the section — the image pops into an already
            // reserved area and nothing shifts. A missing or failed image
            // stays absent, exactly as before.
            if (_pendingTextures.ContainsKey(section.Image))
                DrawPendingFill(min, size);
            return;
        }

        var max = min + size;
        ImGui.GetWindowDrawList().AddImage(texture.Handle, min, max);
        var sourceSize = new Vector2(texture.Width, texture.Height);
        var scalingFactors = size / sourceSize;

        foreach (var graphicBone in section.Bones)
        {
            var bone = skeleton.GetBone(graphicBone.Name);
            var mirrorBoneName = drawMirrors ? GetMirrorBoneName(graphicBone.Name) : null;
            var mirrorBone = mirrorBoneName != null ? skeleton.GetBone(mirrorBoneName) : null;

            // Mirror selection swaps which bone each sided dot addresses;
            // center bones (no counterpart) are unaffected.
            if (SidesSwapped && mirrorBone != null)
                (bone, mirrorBone) = (mirrorBone, bone);

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

    private void DrawBoneAt(IBone bone, Vector2 screenPos)
    {
        // Selection identity is the stable id from the snapshot table; the
        // live bone stays inside the map's rendering walk and never enters a
        // selection command.
        if (!_dotIds.TryGetValue((bone.BoneName, bone.PartialId), out var selectionId))
            return;
        bool matches = MatchesFilter(bone.Name, bone.BoneName);
        _dotCandidates.Add((selectionId, screenPos, bone.Name, matches));
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

    private void ResolveAndDrawDots()
    {
        float s = ImGuiHelpers.GlobalScale;
        var mouse = ImGui.GetMousePos();
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
            if (!candidate.Matches)
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
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
            bool isSelected = _selection.IsSelected(candidate.Id);
            bool isHovered = i == _hoveredDotIndex;
            // Selection is the THEME's primary, not ImGui's style checkmark.
            uint circleColor = isSelected
                ? ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    Crystarium.ActiveTheme.Chrome.Primary))
                : isHovered
                    ? ImGui.GetColorU32(ImGuiCol.Text)
                    : ImGui.GetColorU32(ImGuiCol.TextDisabled);
            // A dot the filter rejects keeps its place — the map is a
            // drawing and its dots ARE the anatomy — and goes faint, which
            // is a map's way of saying what a list says by not listing a row.
            if (!candidate.Matches)
                circleColor = FadeU32(circleColor, FilteredDotOpacity);
            drawList.AddCircleFilled(
                candidate.Pos, CircleRadius * s,
                ImGui.GetColorU32(ImGuiCol.ChildBg));
            drawList.AddCircle(
                candidate.Pos, CircleRadius * s, circleColor);
            if (isSelected || isHovered)
            {
                drawList.AddCircleFilled(
                    candidate.Pos,
                    (CircleRadius - 3f) * s,
                    isSelected
                        ? ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                            Crystarium.ActiveTheme.Chrome.Primary))
                        : ImGui.GetColorU32(ImGuiCol.TextDisabled));
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

        _pendingTextures[imageName] = _textureProvider.CreateFromImageAsync(bytes);
        return null;
    }

    private (IActor? Actor, ActorId? Id) GetSelectedActor()
    {
        // Primary selection decides which actor's maps draw. The stable id
        // resolves to a live actor for this frame's rendering walk only.
        var lineage = _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId.LogicalId,
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } => boneId.Skeleton.Actor.LogicalId,
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor } => gazeActor.LogicalId,
            _ => (Guid?)null,
        };
        if (lineage is { } target)
        {
            foreach (var descriptor in _scene.Snapshot.Actors)
            {
                if (descriptor.Id.LogicalId != target)
                    continue;
                // The Body/Face maps are Character-only: dot identity comes
                // from the Character slot so a same-named auxiliary bone can
                // never be highlighted or selected from a map.
                if (descriptor.CharacterSkeleton is { } skeletonDescriptor)
                {
                    // Extended/IVCS bones get no dot id, and DrawBoneAt draws
                    // nothing without one — the display suppression for the
                    // maps, with the snapshot and selection untouched.
                    bool showNsfw = Config.ConfigurationService.Instance
                        .Config.Display.ShowNsfwBones;
                    foreach (var bone in skeletonDescriptor.Bones)
                    {
                        if (!showNsfw && Core.BoneInfo.BoneInfoService.IsNsfw(
                                bone.Id.CanonicalName))
                            continue;
                        _dotIds[(bone.Id.CanonicalName, bone.Id.PartialId)] =
                            SelectionId.ForBone(bone.Id);
                    }
                }
                // Residual frame-scoped resolution: the maps still render from
                // the live skeleton; the face-map variant read goes through
                // the customize read port with this exact id.
                var resolved = _bindings.Resolve(descriptor.Id);
                return resolved.Success
                    ? (resolved.Value, descriptor.Id)
                    : (null, null);
            }
        }

        // Fall back to first actor; its stable id is the registry's reverse
        // mapping (null before the first committed scene refresh).
        var fallback = _actorManager.Actors.Count > 0 ? _actorManager.Actors[0] : null;
        return (fallback, fallback != null ? _bindings.GetActorId(fallback) : null);
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
