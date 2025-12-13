using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Data;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Window for graphical bone selection using a body map.
/// Users can click on bone circles overlaid on body/face images.
/// </summary>
public class GraphicalBoneWindow : Window, IDisposable
{
    private const float CircleRadius = 6f;
    private const float HitRadius = 18f;

    private readonly ISelectionService _selectionService;
    private readonly IActorManager _actorManager;
    private readonly ISkeletonService _skeletonService;
    private readonly IGPoseService _gPoseService;
    private readonly ITextureProvider _textureProvider;

    private readonly GraphicalBoneConfig _config;
    private readonly Dictionary<string, IDalamudTextureWrap?> _textures = new();

    private int _selectedPage; // 0 = Body, 1 = Face
    private float _closestHoverDistance;
    private IBone? _hoveredBone;
    private bool _swapSides;

    public GraphicalBoneWindow(
        ISelectionService selectionService,
        IActorManager actorManager,
        ISkeletonService skeletonService,
        IGPoseService gPoseService,
        ITextureProvider textureProvider)
        : base($"{Poser.PluginName} - Bone Selection###poser_graphical_bone_window",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _selectionService = selectionService;
        _actorManager = actorManager;
        _skeletonService = skeletonService;
        _gPoseService = gPoseService;
        _textureProvider = textureProvider;

        _config = GraphicalBoneReader.ReadEmbeddedResource();

        Size = new Vector2(900, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(1800, 1200)
        };
    }

    public override bool DrawConditions()
    {
        return _gPoseService.IsGPosing;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UIColors.Background);
    }

    public override void Draw()
    {
        // Reset hover state
        _closestHoverDistance = float.MaxValue;
        _hoveredBone = null;

        // Get selected actor
        var actor = GetSelectedActor();
        if (actor == null)
        {
            ImGui.TextDisabled("Select an actor to pose");
            return;
        }

        var skeleton = _skeletonService.GetSkeleton(actor);
        if (skeleton == null)
        {
            ImGui.TextDisabled("Actor has no skeleton");
            return;
        }

        DrawToolbar();
        ImGui.Separator();
        ImGui.Spacing();

        var contentArea = ImGui.GetContentRegionAvail();

        if (_selectedPage == 0)
        {
            DrawBodyPage(skeleton, contentArea);
        }
        else
        {
            DrawFacePage(skeleton, actor, contentArea);
        }

        // Handle click on hovered bone
        if (_hoveredBone != null && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            _selectionService.Select(_hoveredBone);
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        base.PostDraw();
    }

    private void DrawToolbar()
    {
        // Page selector
        var pages = new[] { "Body", "Face" };
        for (int i = 0; i < pages.Length; i++)
        {
            if (i > 0) ImGui.SameLine();

            bool isSelected = _selectedPage == i;
            if (isSelected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, UIColors.SelectionActive);
            }

            if (ImGui.Button(pages[i], new Vector2(80, 0)))
            {
                _selectedPage = i;
            }

            if (isSelected)
            {
                ImGui.PopStyleColor();
            }
        }

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Swap sides toggle
        if (ImGui.Checkbox("Swap L/R", ref _swapSides))
        {
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Swap left and right sides");
        }
    }

    private void DrawBodyPage(ISkeleton skeleton, Vector2 contentArea)
    {
        float columnWidth = contentArea.X / 3f;

        // Body section
        using (var child = ImRaii.Child("###body_pane", new Vector2(columnWidth, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                DrawBoneSection("body", true, skeleton);
            }
        }

        ImGui.SameLine();

        // Armor section
        using (var child = ImRaii.Child("###armor_pane", new Vector2(columnWidth, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                DrawBoneSection("armor", true, skeleton);
            }
        }

        ImGui.SameLine();

        // Details section (hands, tail, toes)
        using (var child = ImRaii.Child("###details_pane", new Vector2(columnWidth, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                DrawBoneSection("hands", true, skeleton);

                // Check if skeleton has tail
                if (skeleton.GetBone("n_sippo_a") != null)
                {
                    DrawBoneSection("tail", false, skeleton);
                }

                // Check for IVCS toes
                if (skeleton.GetBone("iv_asi_oya_a_l") != null)
                {
                    DrawBoneSection("ivcs_toes", true, skeleton);
                }
            }
        }
    }

    private unsafe void DrawFacePage(ISkeleton skeleton, IActor actor, Vector2 contentArea)
    {
        // Determine race for head image
        string headSection = GetHeadSectionForActor(actor);

        using (var child = ImRaii.Child("###face_pane", new Vector2(-1, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                DrawBoneSection(headSection, true, skeleton);
            }
        }
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

    private void DrawBoneSection(string sectionName, bool drawMirrors, ISkeleton skeleton)
    {
        if (!_config.PoseImages.TryGetValue(sectionName, out var section))
            return;

        var position = ImGui.GetCursorPos();

        // Draw image
        Vector2 imageSize = new(1024, 2048);
        Vector2 scalingFactors = new(0.2f, 0.2f);

        if (!string.IsNullOrEmpty(section.Image))
        {
            DrawImage(section.Image, out imageSize, out scalingFactors);
        }

        var endPosition = ImGui.GetCursorPos();

        // Collect bones to draw
        var drawBones = new List<DrawBoneEntry>();

        foreach (var graphicBone in section.Bones)
        {
            var bone = skeleton.GetBone(graphicBone.Name);
            if (bone == null)
                continue;

            var transformedPosition = position + (graphicBone.PositionVector * scalingFactors);
            if (_swapSides && drawMirrors)
            {
                transformedPosition.X = imageSize.X - transformedPosition.X;
            }

            drawBones.Add(new DrawBoneEntry(bone, transformedPosition));

            // Add mirror bone
            if (drawMirrors)
            {
                var mirrorBoneName = GetMirrorBoneName(graphicBone.Name);
                if (mirrorBoneName != null)
                {
                    var mirrorBone = skeleton.GetBone(mirrorBoneName);
                    if (mirrorBone != null)
                    {
                        var mirrorPosition = position + (graphicBone.PositionVector * scalingFactors);
                        if (!_swapSides)
                        {
                            mirrorPosition.X = imageSize.X - mirrorPosition.X;
                        }
                        drawBones.Add(new DrawBoneEntry(mirrorBone, mirrorPosition));
                    }
                }
            }
        }

        // Draw all bones
        foreach (var entry in drawBones)
        {
            DrawBone(entry, skeleton);
        }

        ImGui.SetCursorPos(endPosition);
    }

    private void DrawBone(DrawBoneEntry entry, ISkeleton skeleton)
    {
        var bone = entry.Bone;
        bool isSelected = _selectionService.IsSelected(bone);
        bool isHovered = _hoveredBone == bone;

        ImGui.SetCursorPos(entry.Position - new Vector2(ImGui.GetFrameHeight() / 2));
        Vector2 screenPos = ImGui.GetCursorScreenPos() + new Vector2(ImGui.GetFrameHeight() / 2);

        // Hit detection
        float mouseDistance = Vector2.Distance(ImGui.GetMousePos(), screenPos);
        if (mouseDistance < HitRadius && mouseDistance < _closestHoverDistance)
        {
            _closestHoverDistance = mouseDistance;
            _hoveredBone = bone;
            isHovered = true;
        }

        var drawList = ImGui.GetWindowDrawList();

        // Circle colors
        uint circleColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        if (isSelected)
        {
            circleColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        }
        else if (isHovered)
        {
            circleColor = ImGui.GetColorU32(ImGuiCol.Text);
        }

        // Draw circle background
        drawList.AddCircleFilled(screenPos, CircleRadius, ImGui.GetColorU32(ImGuiCol.ChildBg));

        // Draw circle outline
        drawList.AddCircle(screenPos, CircleRadius, circleColor);

        // Draw filled center if selected or hovered
        if (isSelected || isHovered)
        {
            var fillColor = isSelected ? ImGui.GetColorU32(ImGuiCol.CheckMark) : ImGui.GetColorU32(ImGuiCol.TextDisabled);
            drawList.AddCircleFilled(screenPos, CircleRadius - 3, fillColor);
        }

        // Tooltip
        if (isHovered && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            ImGui.SetTooltip(bone.Name);
        }
    }

    private void DrawImage(string imageName, out Vector2 imageSizeToFit, out Vector2 scalingFactors)
    {
        var texture = GetTexture(imageName);
        if (texture == null)
        {
            imageSizeToFit = new Vector2(1024, 2048);
            scalingFactors = new Vector2(0.2f, 0.2f);
            return;
        }

        var available = ImGui.GetContentRegionAvail() - (ImGui.GetStyle().FramePadding * 2f);
        var imageSize = new Vector2(texture.Width, texture.Height);
        var aspectRatio = imageSize.X / imageSize.Y;

        imageSizeToFit = new Vector2(available.X, available.X / aspectRatio);
        if (imageSizeToFit.Y > available.Y)
        {
            imageSizeToFit = new Vector2(available.Y * aspectRatio, available.Y);
        }

        ImGui.Image(texture.Handle, imageSizeToFit);

        scalingFactors = new Vector2(
            imageSizeToFit.X / imageSize.X,
            imageSizeToFit.Y / imageSize.Y);
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
        // Check if an actor is selected
        var selected = _selectionService.Primary;
        if (selected is IActor actor)
            return actor;

        // Check if a bone is selected - get its actor
        if (selected is IBone bone)
            return bone.Skeleton.Actor;

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

    private record DrawBoneEntry(IBone Bone, Vector2 Position);
}
