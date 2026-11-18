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
using Poser.Application.World;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;
using Poser.UI.Views;
using Poser.Domain.Library;
using Poser.Documents.Files;
using static Poser.UI.Widgets.PageForm;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI;

/// <summary>The panel beside the library grid that shows the selected file's details.</summary>
internal sealed class LibraryDetailsPanel(EnvironmentControl _environment,
    Poser.Application.Settings.ConfigurationService _configuration)
{
    private string? _detailsPath;
    private SceneMetadataReadOutcome? _detailsMetadata;
    private readonly List<(string Label, string Value)> _detailsRows = [];
    private Vector3? _detailsColor;

    /// <summary>Shows the selected file's location and saved details beside the grid.</summary>
    public float DrawInfoRail(Vector2 origin, Vector2 size, PoseLibraryTileRow? tile)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        if (tile == null)
        {
            // Centred in the rail, both ways, like every empty state.
            TextInBand(
                origin, size, "Select a file",
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.FormHint,
                },
                TextAlign.Center);
            return size.Y;
        }

        if (tile.SceneMetadata is { } scene)
            ProbeDetails(tile.Id, PoseLibraryEntryKind.Scene, scene);

        TextAt(cursor, tile.Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;

        float body = Section(
            "##library-file-info", string.Empty,
            new Vector2(origin.X, cursor.Y), size.X, true, null,
            form =>
            {
                form.ReadOnly("File", tile.Id, help: tile.Id);
                if (tile.SceneMetadata != null)
                    foreach (var (label, value) in _detailsRows)
                        form.ReadOnly(label, value, mono: true);
                else
                    form.ReadOnly("Saved", tile.Modified, mono: true);
                if (!string.IsNullOrEmpty(tile.Author))
                    form.ReadOnly("Author", tile.Author!, mono: true);
                if (tile.Tags.Count > 0)
                    form.ReadOnly("Tags", string.Join(", ", tile.Tags),
                        mono: true);
                if (tile.Flagged)
                    form.Status(tile.StatusText);
            },
            divider: false, dense: true);
        return cursor.Y - origin.Y + body + theme.Spacing.Six * scale;
    }

    public void DrawObjectsRail(Vector2 origin, Vector2 size, PoseLibraryTileRow tile, PoseLibraryEntryKind kind)
    {
        ProbeDetails(tile.ThumbKey, kind);

        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        // The entry's name comes first, then a plain "Properties" heading,
        // with no dividers anywhere in this panel.
        TextAt(cursor, tile.Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;
        TextAt(cursor, "Properties", new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormHint,
        });
        cursor.Y += (theme.Typography.CaptionSize + 6f) * scale;

        Section(
            "##objects-inspector", string.Empty,
            new Vector2(origin.X, cursor.Y), size.X, true, null,
            form =>
            {
                if (_detailsColor is { } color)
                {
                    form.Custom("Color", 20f, row =>
                    {
                        float radius = 8f * ImGuiHelpers.GlobalScale;
                        var center = row.CenterControl(16f)
                            + new Vector2(radius, radius);
                        var clamped = Vector3.Clamp(
                            color, Vector3.Zero, Vector3.One);
                        ImGui.GetWindowDrawList().AddCircleFilled(
                            center, radius,
                            ImGui.ColorConvertFloat4ToU32(
                                new Vector4(clamped, 1f)));
                    });
                }
                form.ReadOnly("File", tile.Id, help: tile.Id);
                foreach (var (label, value) in _detailsRows)
                    form.ReadOnly(label, value, mono: true);
            },
            divider: false, dense: true);
    }

    private void ProbeDetails(string path, PoseLibraryEntryKind kind, SceneMetadataReadOutcome? supplied = null)
    {
        if (string.Equals(path, _detailsPath, StringComparison.Ordinal) && ReferenceEquals(supplied, _detailsMetadata))
            return;
        _detailsPath = path;
        _detailsMetadata = supplied;
        _detailsRows.Clear();
        _detailsColor = null;
        try
        {
            switch (kind)
            {
                case PoseLibraryEntryKind.Scene:
                case PoseLibraryEntryKind.Actor:
                case PoseLibraryEntryKind.Environment:
                case PoseLibraryEntryKind.Overlay:
                case PoseLibraryEntryKind.Group:
                case PoseLibraryEntryKind.WorldObject:
                case PoseLibraryEntryKind.Prop:
                case PoseLibraryEntryKind.Light:
                case PoseLibraryEntryKind.Camera:
                    var metadata = supplied ?? SceneFileStore.Default.ReadMetadata(path);
                    if (metadata.Succeeded)
                    {
                        if (!string.IsNullOrEmpty(metadata.PlaceName))
                            _detailsRows.Add(("Place", metadata.PlaceName!));
                        else if (metadata.TerritoryId != 0)
                            _detailsRows.Add(("Place", $"Territory {metadata.TerritoryId}"));
                        if (kind == PoseLibraryEntryKind.Group)
                            AppendContentsRows(metadata, _detailsRows);
                        if (kind is PoseLibraryEntryKind.Scene or PoseLibraryEntryKind.Environment)
                        {
                            // Older scenes saved only the weather's id, so look its name up in the game data.
                            string weather = metadata.WeatherName.Length > 0
                                ? metadata.WeatherName
                                : metadata.WeatherId != 0 &&
                                  _environment.GetWeatherInfo(
                                      metadata.WeatherId) is { } known
                                    ? known.Name
                                    : string.Empty;
                            if (weather.Length > 0)
                                _detailsRows.Add(("Weather", weather));
                        }
                        if (kind == PoseLibraryEntryKind.Scene)
                            AppendSceneRows(metadata, path, _detailsRows);
                        if (!string.IsNullOrWhiteSpace(metadata.Description))
                            _detailsRows.Add(("Description", metadata.Description));
                        if (metadata.SavedAt is { } saved)
                            _detailsRows.Add(("Saved", saved.ToLocalTime()
                                .ToString(LibraryStamp.DateTimeFormat,
                                    CultureInfo.InvariantCulture)));
                    }
                    break;
            }
            // Older files use their modification date when no saved date was recorded.
            if (!_detailsRows.Exists(row => row.Label == "Saved"))
                _detailsRows.Add(("Saved",
                    System.IO.File.GetLastWriteTime(path).ToString(
                        LibraryStamp.DateTimeFormat,
                        CultureInfo.InvariantCulture)));
        }
        catch (Exception)
        {
            _detailsRows.Add(("Details", "could not be read"));
        }
    }

    /// <summary>What a scene holds, every count shown even when it is zero, and how its
    /// actors' looks will load.</summary>
    private void AppendSceneRows(SceneMetadataReadOutcome scene, string path,
        List<(string Label, string Value)> rows)
    {
        if (scene.MinuteOfDay is { } minute)
            rows.Add(("Time of day", $"{minute / 60:00}:{minute % 60:00}"));
        string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
        rows.Add(("Actors", Count(scene.ActorCount)));
        rows.Add(("MCDFs", Count(scene.McdfCount)));
        // Scene appearances: the actors whose saved look is not an MCDF.
        rows.Add(("Appearances", Count(scene.SceneAppearanceCount)));
        rows.Add(("Objects", Count(scene.PropCount)));
        rows.Add(("World objects", Count(scene.WorldObjectCount)));
        rows.Add(("Lights", Count(scene.LightCount)));
        rows.Add(("Cameras", Count(scene.CameraCount)));
        rows.Add(("Overlays", Count(scene.OverlayCount)));
        rows.Add(("Groups", Count(scene.GroupCount)));
        rows.Add(("Animation", scene.AnimationSaved ? "Saved" : "Not saved"));
        // Decides whether scene appearances load editable or as MCDFs.
        rows.Add(("Saved on", scene.SavedBy is not { } savedBy || savedBy == _configuration.Config.Library.InstallId
            ? "This computer"
            : "Another computer"));
        rows.Add(("Size", (new System.IO.FileInfo(path).Length / 1048576.0)
            .ToString("0.#", CultureInfo.InvariantCulture) + " MB"));
    }

    /// <summary>The entry's contents as one row per kind ("Actors 2",
    /// "Lights 3"), rather than one line that gets cut off.</summary>
    private static void AppendContentsRows(
        SceneMetadataReadOutcome metadata,
        List<(string Label, string Value)> rows)
    {
        void Part(int count, string label)
        {
            if (count > 0)
                rows.Add((label,
                    count.ToString(CultureInfo.InvariantCulture)));
        }
        Part(metadata.ActorCount, "Actors");
        Part(metadata.PropCount, "Objects");
        Part(metadata.WorldObjectCount, "World objects");
        Part(metadata.LightCount, "Lights");
        Part(metadata.CameraCount, "Cameras");
        Part(metadata.OverlayCount, "Overlays");
    }
}

