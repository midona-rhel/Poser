using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Library;

namespace Poser.UI;

public sealed partial class PoseLibraryPane
{
    private bool _sourceHealthOpen;

    private void OpenSourceHealth() => _sourceHealthOpen = true;

    public void OpenLibraryInExplorer()
    {
        try
        {
            var root = _config.Config.Library.ResolveRoot();
            if (!LibraryConfiguration.TryEnsureDirectory(root, out var detail))
            {
                _notices.Failed(detail);
                _library.RequestScan();
                return;
            }
            OpenSourceFolder(root);
            _library.RequestScan();
        }
        catch (Exception ex)
        {
            _notices.Failed("Open in Explorer: " + ex.Message);
        }
    }

    private void OpenSourceFolder(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                _notices.Failed($"Folder '{path}' is missing or inaccessible. Retry the source scan.");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notices.Failed($"Could not open '{path}': {ex.Message}");
        }
    }

    private bool SourceStillConfigured(PoseLibrarySourceSnapshot source)
    {
        var sources = _config.Config.Library.Sources;
        if (source.Index >= 0 && source.Index < sources.Count
            && sources[source.Index].Name == source.Name
            && sources[source.Index].Path == source.Path
            && sources[source.Index].Enabled == source.Enabled)
            return true;
        _notices.Refused("The sources changed. Retry to see their current state.");
        _library.RequestScan();
        return false;
    }

    private void CreateSource(PoseLibrarySourceSnapshot source)
    {
        if (!SourceStillConfigured(source))
            return;
        if (!LibraryConfiguration.TryEnsureDirectory(source.Path, out var detail))
            _notices.Failed(detail);
        _library.RequestScan();
    }

    private void DisableSource(PoseLibrarySourceSnapshot source)
    {
        if (!SourceStillConfigured(source))
            return;
        _config.Config.Library.Sources[source.Index].Enabled = false;
        _config.Save();
    }

    private void DrawSourceHealthModal()
    {
        if (!_sourceHealthOpen)
            return;
        Crystarium.Modal(
            "##library-source-health", _sourceHealthOpen,
            next => _sourceHealthOpen = next, "Library sources",
            height: 480f,
            body: () =>
            {
                var scale = ImGuiHelpers.GlobalScale;
                var available = ImGui.GetContentRegionAvail();
                Crystarium.ScrollRegion(
                    "##library-source-health-rows",
                    available.X / scale, MathF.Max(1f, available.Y / scale),
                    region => DrawSourceHealthRows(region.ContentWidth));
            },
            footer: () =>
            {
                Crystarium.Button("Retry", _library.RequestScan,
                    disabled: _library.IsScanning, id: "source-health-retry");
                ImGui.SameLine();
                if (Crystarium.Button("Source settings", id: "source-health-settings"))
                {
                    _sourceHealthOpen = false;
                    OnSettingsRequested?.Invoke();
                }
                ImGui.SameLine();
                if (Crystarium.Button("Close", id: "source-health-close"))
                    _sourceHealthOpen = false;
            });
    }

    private void DrawSourceHealthRows(float width)
    {
        var snapshot = _library.Snapshot;
        var scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var style = new TextStyle { Size = theme.Typography.BodySize, Color = theme.Text };
        if (snapshot.SkippedSourceCount > 0)
            SourceParagraph(
                $"{snapshot.SkippedSourceCount} additional sources were not scanned. " +
                $"Keep at most {PoseLibraryLimits.MaxSources} configured sources; remove extras in Source settings.",
                width, style, scale);
        foreach (var source in snapshot.Sources)
        {
            ImGui.PushID(source.Index);
            try
            {
                var name = string.IsNullOrWhiteSpace(source.Name)
                    ? $"Source {source.Index + 1}" : source.Name;
                SourceParagraph(name + " — " + source.Health, width, style, scale);
                SourceParagraph(source.Path.Length == 0 ? "(blank path)" : source.Path,
                    width, style, scale);
                if (source.Detail.Length > 0)
                    SourceParagraph(source.Detail, width, style, scale);
                if (Crystarium.Button("Copy path", id: "copy-source-path"))
                    ImGui.SetClipboardText(source.Path);
                if (source.Health == PoseLibrarySourceHealth.Missing)
                {
                    ImGui.SameLine();
                    if (Crystarium.Button("Create", id: "create-source"))
                        CreateSource(source);
                }
                if (source.Health == PoseLibrarySourceHealth.Ready)
                {
                    ImGui.SameLine();
                    if (Crystarium.Button("Open", id: "open-source")
                        && SourceStillConfigured(source))
                        OpenSourceFolder(source.Path);
                }
                if (source.Enabled)
                {
                    ImGui.SameLine();
                    if (Crystarium.Button("Disable", id: "disable-source"))
                        DisableSource(source);
                }
                ImGui.Dummy(new Vector2(0f, theme.Spacing.Three * scale));
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    private static void SourceParagraph(string text, float width, TextStyle style, float scale)
    {
        width = MathF.Max(1f, width);
        // The normal text primitive deliberately lets a long word overflow.
        // Paths often contain no spaces, so insert display-only line breaks;
        // Copy path above always copies the untouched configured value.
        var display = new StringBuilder();
        foreach (var paragraph in text.Split('\n'))
        {
            var start = 0;
            while (start < paragraph.Length)
            {
                var low = 1;
                var high = paragraph.Length - start;
                while (low < high)
                {
                    var middle = (low + high + 1) / 2;
                    if (Crystarium.MeasureText(paragraph.Substring(start, middle), style).X <= width * scale)
                        low = middle;
                    else
                        high = middle - 1;
                }
                if (start + low < paragraph.Length && char.IsHighSurrogate(paragraph[start + low - 1]))
                    low = low > 1 ? low - 1 : Math.Min(2, paragraph.Length - start);
                display.Append(paragraph, start, low);
                display.Append('\n');
                start += low;
            }
        }
        var rendered = display.ToString().TrimEnd('\n');
        var constraint = TextConstraint.Wrap(width, whitespace: TextWhitespace.PreWrap);
        var size = Crystarium.MeasureText(rendered, style, constraint);
        var height = MathF.Max(size.Y, (style.Size ?? 14f) * scale);
        Crystarium.TextInBand(ImGui.GetCursorScreenPos(),
            new Vector2(width * scale, height), rendered, style, constraint, TextAlign.Start);
        ImGui.Dummy(new Vector2(width * scale, height));
    }
}
