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
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Files;
using Poser.Library;
using Poser.Services;
using Poser.Domain.Scene;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The info rail and the objects rail beside the grid.</summary>
internal sealed class LibraryDetailsPanel(IEnvironmentControl _environment)
{
    private string? _detailsPath;
    private readonly List<(string Label, string Value)> _detailsRows = [];
    private Vector3? _detailsColor;

    /// <summary>The rail for the tabs that cannot preview (MCDF, scenes):
    /// the selected FILE, stated — name, stamp, author, contents, tags.
    /// Returns the height consumed so a caller can stack below it.</summary>
    public float DrawInfoRail(Vector2 origin, Vector2 size, PoseLibraryTileRow? tile,
        string modified, string contents)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        if (tile == null)
        {
            // Centred in the rail, both ways, like every empty state.
            Crystarium.TextInBand(
                origin, size, "Select a file",
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.FormHint,
                },
                TextAlign.Center);
            return size.Y;
        }

        Crystarium.TextAt(cursor, tile.Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;

        float body = Crystarium.Section(
            "##library-file-info", string.Empty,
            new Vector2(origin.X, cursor.Y), size.X, true, null,
            form =>
            {
                form.ReadOnly("Saved", modified,
                    mono: true);
                if (!string.IsNullOrEmpty(tile.Author))
                    form.ReadOnly("Author", tile.Author!, mono: true);
                // Contents are PER-KIND rows — "Actors 2", "Lights 3" —
                // never one truncating line (ruled 2026-08-31). The
                // pre-minted one-liner splits on its own separator.
                if (contents.Length > 0)
                    foreach (var part in contents.Split(", "))
                    {
                        int space = part.IndexOf(' ');
                        if (space > 0 && int.TryParse(
                                part[..space], out _))
                            form.ReadOnly(
                                char.ToUpperInvariant(part[space + 1])
                                    + part[(space + 2)..],
                                part[..space], mono: true);
                        else
                            form.ReadOnly("Contents", part, mono: true);
                    }
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
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        // The entry's NAME leads, then one plain "Properties" heading — no
        // separators anywhere on this rail.
        Crystarium.TextAt(cursor, tile.Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;
        Crystarium.TextAt(cursor, "Properties", new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormHint,
        });
        cursor.Y += (theme.Typography.CaptionSize + 6f) * scale;

        Crystarium.Section(
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
                foreach (var (label, value) in _detailsRows)
                    form.ReadOnly(label, value, mono: true);
            },
            divider: false, dense: true);
    }

    private void ProbeDetails(string path, PoseLibraryEntryKind kind)
    {
        if (string.Equals(path, _detailsPath, StringComparison.Ordinal))
            return;
        _detailsPath = path;
        _detailsRows.Clear();
        _detailsColor = null;
        try
        {
            switch (kind)
            {
                case PoseLibraryEntryKind.Actor:
                case PoseLibraryEntryKind.Environment:
                case PoseLibraryEntryKind.Overlay:
                case PoseLibraryEntryKind.Group:
                case PoseLibraryEntryKind.WorldObject:
                case PoseLibraryEntryKind.Prop:
                case PoseLibraryEntryKind.Light:
                case PoseLibraryEntryKind.Camera:
                    var metadata = SceneFileStore.Default.ReadMetadata(path);
                    if (metadata.Succeeded)
                    {
                        if (!string.IsNullOrEmpty(metadata.PlaceName))
                            _detailsRows.Add(("Place", metadata.PlaceName!));
                        // A group entry says what it HOLDS — the one fact
                        // its tile cannot — as per-kind rows.
                        if (kind == PoseLibraryEntryKind.Group)
                            AppendContentsRows(metadata, _detailsRows);
                        if (kind == PoseLibraryEntryKind.Environment)
                        {
                            // The name travels in the file when the capture
                            // recorded it; an older file resolves through the
                            // live weather sheet by id.
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
                        if (metadata.SavedAt is { } saved)
                            _detailsRows.Add(("Saved", saved.ToLocalTime()
                                .ToString(LibraryStamp.DateTimeFormat,
                                    CultureInfo.InvariantCulture)));
                    }
                    break;
            }
            // Every entry answers "Saved": the document's own stamp when it
            // records one, else the file's write time — a light or camera
            // document carries no date of its own.
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
        if (_detailsRows.Count == 0 && _detailsColor is null)
            _detailsRows.Add(("Details", "none recorded"));
    }

    /// <summary>The entry's contents as PER-KIND rows — "Actors 2",
    /// "Lights 3" — never one truncating line.</summary>
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
        Part(metadata.WorldObjectCount, "Borrowed objects");
        Part(metadata.LightCount, "Lights");
        Part(metadata.CameraCount, "Cameras");
        Part(metadata.OverlayCount, "Overlays");
    }
}

