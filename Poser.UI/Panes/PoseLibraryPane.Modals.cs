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
                    var gone = _fileOperations.Delete(more);
                    if (!gone.Succeeded)
                        _notices.Failed("Delete", gone.Detail);
                }
                _deleteMore.Clear();
                var result = _fileOperations.Delete(_deletePath);
                if (result.Succeeded)
                {
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
