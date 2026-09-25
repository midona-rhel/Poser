using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Files;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The metadata and delete modals.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>The metadata modal: author and comma-separated tags, written
    /// back into the pose file through the atomic store (Brio's
    /// SaveMetadata flow). The core normalizes the tags; the typed outcome
    /// lands in the note.</summary>
    private void DrawMetadataModal()
    {
        if (!_metaOpen)
            return;
        Crystarium.Modal(
            "##library-metadata",
            _metaOpen,
            next => _metaOpen = next,
            "Edit metadata",
            height: 400f,
            body: () =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance = (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Author", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-author", _metaAuthor,
                next => _metaAuthor = next,
                placeholder: "Author");
            ImGui.Dummy(new Vector2(0f, rowGap));

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                "Tags (comma-separated)", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-tags", _metaTags,
                next => _metaTags = next,
                placeholder: "tag, tag");
            ImGui.Dummy(new Vector2(0f, rowGap));

            // Description and the preview image have serialized on both sides
            // since the format existed; only the editor was missing.
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Description", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-description", _metaDescription,
                next => _metaDescription = next,
                placeholder: "What this pose is for");
            ImGui.Dummy(new Vector2(0f, rowGap));

            bool willHaveImage = _metaImage.Remove
                ? false
                : _metaImage.Base64 is { Length: > 0 } || _metaHadImage;
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                willHaveImage ? "Preview image: stored" : "Preview image: none",
                captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    willHaveImage ? "Replace image" : "Add image",
                    style: pairStyle,
                    id: "library-metadata-image-set"))
                _metaImageBrowser.Open(
                    _lastImageFolder,
                    chosen =>
                    {
                        _lastImageFolder =
                            System.IO.Path.GetDirectoryName(chosen)
                            ?? _lastImageFolder;
                        var read = PoseLibraryFileActions.ReadPreviewImage(
                            chosen, out var encoded);
                        if (read.Succeeded && encoded is { Length: > 0 })
                            _metaImage = PosePreviewImageEdit.Set(encoded);
                        else
                            _notices.Failed("Preview image", read.Detail);
                    });
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Remove image",
                    style: pairStyle,
                    disabled: !willHaveImage,
                    id: "library-metadata-image-clear"))
                _metaImage = PosePreviewImageEdit.Cleared;
            ImGui.Dummy(new Vector2(0f, rowGap));

            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    id: "library-metadata-confirm"))
            {
                var result = PoseLibraryFileActions.Default.EditMetadata(
                    _metaPath,
                    _metaAuthor,
                    _metaTags.Split(','),
                    _metaDescription,
                    _metaImage);
                if (result.Succeeded)
                {
                    // The thumbnail cache keys on the path, and the path did
                    // not change: an edited preview would keep drawing the
                    // image the file no longer carries. Only the visible page
                    // decodes again, and only when the image was actually
                    // touched — an author or tag edit leaves the grid alone.
                    if (_metaImage.Remove || _metaImage.Base64 is { Length: > 0 })
                        _thumbs.Clear();
                    _library.RequestScan();
                }
                else
                    _notices.Failed("Metadata", result.Detail);
                _metaOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-metadata-cancel"))
                _metaOpen = false;
        });
    }

    /// <summary>The delete confirm: destructive, so it is never a bare menu
    /// click. Deleting an auto-save re-enumerates that tab; anything else
    /// rescans the library.</summary>
    private void DrawDeleteModal()
    {
        if (!_deleteOpen)
            return;
        Crystarium.Modal(
            "##library-delete",
            _deleteOpen,
            next => _deleteOpen = next,
            "Delete file",
            height: 180f,
            body: () =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance = (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), _deleteName,
                new TextStyle
                {
                    Size = theme.Typography.BodySize,
                    Color = theme.Text,
                });
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                "This permanently deletes the file from disk.",
                captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            ImGui.Dummy(new Vector2(0f, rowGap));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    "Delete",
                    variant: ButtonVariant.Danger,
                    style: pairStyle,
                    id: "library-delete-confirm"))
            {
                // The rest of a bulk delete goes first; the clicked file's
                // own outcome is the one reported below.
                foreach (var more in _deleteMore)
                {
                    var gone = PoseLibraryFileActions.Default.Delete(more);
                    if (gone.Succeeded)
                        FavoritePathChanged(more, null);
                    else
                        _notices.Failed("Delete", gone.Detail);
                }
                _deleteMore.Clear();
                var result = PoseLibraryFileActions.Default.Delete(_deletePath);
                if (result.Succeeded)
                {
                    FavoritePathChanged(_deletePath, null);
                    if (_type == LibraryType.AutoSaves)
                        _autoDirty = true;
                    else
                        _library.RequestScan();
                }
                else
                    _notices.Failed("Delete", result.Detail);
                _deleteOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-delete-cancel"))
                _deleteOpen = false;
        });
    }
}
