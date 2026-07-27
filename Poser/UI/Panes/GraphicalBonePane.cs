using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
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

    // M11 marquee (Anamnesis MouseCanvas): dot positions recorded per frame,
    // drag on empty canvas selects everything inside the rectangle.
    private readonly System.Collections.Generic.List<(SelectionId Id, Vector2 Pos)> _frameDots = new();
    private readonly List<(SelectionId Id, Vector2 Pos, string Name)> _dotCandidates = new();
    private Vector2? _marqueeStart;
    private readonly IActorManager _actorManager;
    private readonly ISkeletonService _skeletonService;
    private readonly ITextureProvider _textureProvider;

    private readonly GraphicalBoneConfig _config;
    private readonly Dictionary<string, IDalamudTextureWrap?> _textures = new();

    /// <summary>
    /// Mirror selection (Brio GraphicalSidesSwapped): swaps which side each
    /// map dot addresses, so the pose can be edited as seen from the front.
    /// Applies to the graphical maps only — never the tree, matrix, 3D view,
    /// or overlay.
    /// </summary>
    public bool SidesSwapped { get; set; }

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
        ITextureProvider textureProvider)
    {
        _scene = scene;
        _selection = scene.Selection;
        _bindings = bindings;
        _actorManager = actorManager;
        _skeletonService = skeletonService;
        _textureProvider = textureProvider;

        _config = GraphicalBoneReader.ReadEmbeddedResource();

    }

    /// <summary>
    /// Renders the Body (0) or Face (1) map inline inside the AppShell Pose
    /// surface (M2: the seg swaps the pose surface — no window detour).
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

        var actor = GetSelectedActor();
        if (actor == null)
            return false;
        var skeleton = _skeletonService.GetSkeleton(actor);
        if (skeleton == null)
            return false;

        var origin = ImGui.GetCursorScreenPos();

        if (page == 0)
            DrawBodyPage(skeleton, contentArea);
        else
            DrawFacePage(skeleton, actor, contentArea);
        ResolveAndDrawDots();

        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
            && ImGui.IsMouseHoveringRect(origin, origin + contentArea);

        if (_hoveredBone is { } hoveredId && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hovered)
        {
            if (ImGui.GetIO().KeyCtrl)
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
                    fg.AddRectFilled(rmin, rmax, ImGui.ColorConvertFloat4ToU32(new Vector4(50 / 255f, 151 / 255f, 1f, 0.12f)));
                    fg.AddRect(rmin, rmax, ImGui.ColorConvertFloat4ToU32(new Vector4(50 / 255f, 151 / 255f, 1f, 0.65f)));
                }
            }
            else
            {
                if (isDrag)
                {
                    if (!ImGui.GetIO().KeyCtrl)
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

        if (skeleton.GetBone("iv_asi_oya_a_l") != null)
        {
            DrawBoneSectionAt(
                "ivcs_toes",
                Slot(1454f, 931f, 600f, 216f),
                drawMirrors: true,
                skeleton);
        }
    }

    private unsafe void DrawFacePage(ISkeleton skeleton, IActor actor, Vector2 contentArea)
    {
        string headSection = GetHeadSectionForActor(actor);
        if (!_config.PoseImages.TryGetValue(headSection, out var section) ||
            string.IsNullOrEmpty(section.Image))
            return;
        var texture = GetTexture(section.Image);
        if (texture == null)
            return;

        float s = ImGuiHelpers.GlobalScale;
        float margin = 12f * s;
        var viewportOrigin = ImGui.GetCursorScreenPos();
        var available = Vector2.Max(
            Vector2.One,
            contentArea - new Vector2(margin * 2f));
        var sourceSize = new Vector2(texture.Width, texture.Height);
        float fit = MathF.Min(
            available.X / sourceSize.X,
            available.Y / sourceSize.Y);
        var imageSize = sourceSize * fit;
        var imageOrigin =
            viewportOrigin + (contentArea - imageSize) * 0.5f;
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

    private unsafe string GetHeadSectionForActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return "human_head";

        try
        {
            var character = (Character*)actor.Address;
            if (character == null)
                return "human_head";

            var customize = character->DrawData.CustomizeData;
            var race = customize.Race;

            return race switch
            {
                1 => "human_head",     // Hyur
                2 => "human_head",     // Elezen
                3 => "human_head",     // Lalafell
                4 => "miqote_head",    // Miqo'te
                5 => "human_head",     // Roegadyn
                6 => "human_head",     // Au Ra
                7 => "hrothgar_head",  // Hrothgar
                8 => "viera_head_a",   // Viera (default ear type)
                _ => "human_head"
            };
        }
        catch
        {
            return "human_head";
        }
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
        if (texture == null)
            return;

        var min = new Vector2(rect.X, rect.Y);
        var size = new Vector2(rect.Z, rect.W);
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
        _dotCandidates.Add((selectionId, screenPos, bone.Name));
        _frameDots.Add((selectionId, screenPos));
    }

    private void ResolveAndDrawDots()
    {
        float s = ImGuiHelpers.GlobalScale;
        var mouse = ImGui.GetMousePos();
        for (int i = 0; i < _dotCandidates.Count; i++)
        {
            var candidate = _dotCandidates[i];
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
            uint circleColor = isSelected
                ? ImGui.GetColorU32(ImGuiCol.CheckMark)
                : isHovered
                    ? ImGui.GetColorU32(ImGuiCol.Text)
                    : ImGui.GetColorU32(ImGuiCol.TextDisabled);
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
                        ? ImGui.GetColorU32(ImGuiCol.CheckMark)
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

    private IDalamudTextureWrap? GetTexture(string imageName)
    {
        if (_textures.TryGetValue(imageName, out var cached))
            return cached;

        var bytes = GraphicalBoneReader.GetImageBytes(imageName);
        if (bytes == null)
        {
            _textures[imageName] = null;
            return null;
        }

        try
        {
            var task = _textureProvider.CreateFromImageAsync(bytes);
            task.Wait();
            var texture = task.Result;
            _textures[imageName] = texture;
            return texture;
        }
        catch
        {
            _textures[imageName] = null;
            return null;
        }
    }

    private IActor? GetSelectedActor()
    {
        // Primary selection decides which actor's maps draw. The stable id
        // resolves to a live actor for this frame's rendering walk only.
        var lineage = _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId.LogicalId,
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } => boneId.Skeleton.Actor.LogicalId,
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
                    foreach (var bone in skeletonDescriptor.Bones)
                        _dotIds[(bone.Id.CanonicalName, bone.Id.PartialId)] =
                            SelectionId.ForBone(bone.Id);
                }
                // Residual frame-scoped resolution: the maps still render from
                // the live skeleton and read the face-map variant from actor
                // customize data (display formatting, not selection identity).
                var resolved = _bindings.Resolve(descriptor.Id);
                return resolved.Success ? resolved.Value : null;
            }
        }

        // Fall back to first actor
        return _actorManager.Actors.Count > 0 ? _actorManager.Actors[0] : null;
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
    }
}
