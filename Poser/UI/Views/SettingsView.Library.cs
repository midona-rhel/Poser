using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Library;

namespace Poser.UI.Views;

public static partial class SettingsView
{
    private static void DrawLibrary(SettingsViewModel vm, Crystarium.PageScope page)
    {
        var issues = vm.SourceSnapshot is { } snapshot
            ? vm.Library.Issues(snapshot, vm.SavedLibrary) : Array.Empty<LibrarySourceIssue>();
        int skipped = vm.SourceSnapshot?.SkippedSourceCount ?? 0;
        if (vm.ShowSourceIssues)
        {
            DrawSourceIssues(vm, page, issues, skipped);
            return;
        }
        page.Section("Poser folder", form =>
        {
            form.TextInputActions("Folder", vm.Library.Root, next => vm.Library.Root = next,
                actions => actions.Button("Browse", () => vm.OnBrowseFolder?.Invoke(
                    vm.Library.EffectiveRoot, next => vm.Library.Root = next)),
                placeholder: LibraryConfiguration.DefaultRoot,
                help: "One root for the fixed Poses, Objects, Scenes and MCDFs homes. Save applies changes.");
        }, divider: false);
        page.Section("Pose library", form =>
        {
            form.Switch("Use library for Import", vm.UseLibraryWhenImporting,
                next => vm.UseLibraryWhenImporting = next,
                "Import buttons open the pose library instead of the file dialog");
            form.Switch("Show file extensions", vm.LibraryShowExtensions,
                next => vm.LibraryShowExtensions = next, "Tile names carry .pose / .cmp");
        }, divider: false);
        page.Section("Source folders", form =>
        {
            LibrarySourceDraft? removing = null;
            for (int i = 0; i < vm.Library.Sources.Count; i++)
            {
                var source = vm.Library.Sources[i];
                ImGui.PushID(i);
                try
                {
                    if (source.IsCustom)
                    {
                        form.TextInput("Name", source.Name, next => source.Name = next);
                        form.TextInput("Folder", source.Path, next => source.Path = next,
                            help: source.Path);
                        form.SwitchActions("Enabled", source.Enabled, next => source.Enabled = next,
                            actions => actions.Button("Remove", () => removing = source,
                                help: "Remove this custom source when Settings is saved"));
                    }
                    else
                    {
                        form.ReadOnly(source.Name, vm.Library.PathFor(source),
                            help: vm.Library.PathFor(source));
                    }
                    var health = vm.SourceSnapshot is { } current
                        ? vm.Library.RowHealth(source, current, vm.SavedLibrary) : null;
                    if (vm.Library.IsPending(source))
                        form.Status("Pending Save — this draft has not been scanned.");
                    else if (LibrarySettingsDraft.IsFailure(health))
                        SourceError(form, FailureReason(health!), health!.Path + "\n" + health.Detail);
                    else if (!source.Enabled)
                        form.Status("Disabled in saved settings.");
                    else if (health is null || health.Health == PoseLibrarySourceHealth.Unscanned)
                        form.Status("Waiting for saved-source scan.");
                    form.Actions(string.Empty, actions =>
                    {
                        actions.Button("Copy path", () => ImGui.SetClipboardText(vm.Library.PathFor(source)));
                        actions.Button("Open", () => vm.OnOpenSource?.Invoke(source),
                            disabled: vm.Library.IsPending(source) || health?.Health != PoseLibrarySourceHealth.Ready,
                            help: "Open the saved source without creating folders");
                    });
                }
                finally { ImGui.PopID(); }
            }
            if (removing is not null)
                vm.Library.Remove(removing);
            form.TextInput("New source name", vm.LibraryNewName, next => vm.LibraryNewName = next,
                placeholder: "Taken from the folder when left blank");
            form.TextInput("New source folder", vm.LibraryNewPath, next => vm.LibraryNewPath = next,
                placeholder: "Full path to a folder of poses");
            form.Actions(string.Empty, actions =>
            {
                actions.Button("Add source", () => AddLibrarySource(vm),
                    disabled: string.IsNullOrWhiteSpace(vm.LibraryNewPath));
                actions.Button("Source issues", () => ShowSourceIssues(vm, true),
                    disabled: issues.Count == 0 && skipped == 0);
                actions.Button("Retry", () => vm.OnRetrySources?.Invoke(), disabled: vm.SourceScanBusy,
                    help: "Rescan saved paths; draft edits are applied only on Save");
            });
            if (skipped > 0)
                SourceError(form, $"{skipped} sources exceed library capacity.", "Open Source issues for details.");
            if (vm.LibraryStatus.Length > 0)
                form.Paragraph(vm.LibraryStatus);
        });
    }

    private static void DrawSourceIssues(SettingsViewModel vm, Crystarium.PageScope page,
        IReadOnlyList<LibrarySourceIssue> issues, int skipped)
    {
        page.Section("Source issues", form =>
        {
            form.Actions(string.Empty, actions =>
            {
                actions.Button("Back to source folders", () => ShowSourceIssues(vm, false));
                actions.Button("Retry", () => vm.OnRetrySources?.Invoke(), disabled: vm.SourceScanBusy);
            });
            if (issues.Count == 0 && skipped == 0)
                form.Paragraph("Source issues resolved. Return to source folders or close Settings.");
            if (skipped > 0)
                SourceParagraph(vm, form, $"{skipped} additional sources were not scanned. " +
                    $"Keep at most {PoseLibraryLimits.MaxSources} configured sources; remove extra custom sources and Save.");
            foreach (var issue in issues)
            {
                var source = issue.Source;
                ImGui.PushID(source.SavedIndex);
                try
                {
                    SourceError(form, issue.Health.Name + " — " + FailureReason(issue.Health), issue.Health.Detail);
                    SourceParagraph(vm, form, "Saved path: " + issue.Health.Path);
                    SourceParagraph(vm, form, issue.Health.Detail);
                    if (issue.PendingSave)
                        form.Paragraph("Pending Save — these are the saved path's results, not validation of your draft. Save or cancel to apply or discard edits.");
                    else if (source.Kind is LibrarySourceKind.Brio or LibrarySourceKind.Anamnesis)
                        form.Paragraph("System-provided reference. Configure this folder in its owning tool, then Retry. Poser does not create third-party folders.");
                    form.Actions(string.Empty, actions =>
                    {
                        actions.Button("Copy path", () => ImGui.SetClipboardText(issue.Health.Path));
                        if (vm.Library.CanRepair(issue, vm.SavedLibrary))
                            actions.Button("Create folder", () => vm.OnRepairSource?.Invoke(issue),
                                help: "Create this exact saved folder now. Cancel does not undo filesystem repair.");
                    });
                    if (source.IsCustom && !issue.PendingSave)
                        form.Actions(string.Empty, actions =>
                        {
                            actions.Button("Edit source", () => ShowSourceIssues(vm, false));
                            actions.Button("Disable", () => source.Enabled = false,
                                help: "Disable this custom source on Save");
                            actions.Button("Remove", () => vm.Library.Remove(source),
                                help: "Remove this custom source on Save; files are untouched");
                        });
                }
                finally { ImGui.PopID(); }
            }
            if (vm.LibraryStatus.Length > 0)
                SourceParagraph(vm, form, vm.LibraryStatus);
        }, divider: false);
    }

    private static void ShowSourceIssues(SettingsViewModel vm, bool open)
    {
        vm.ShowSourceIssues = open;
        vm.ResetPageScroll = true;
    }

    private static string FailureReason(PoseLibrarySourceSnapshot health) => health.Health switch
    {
        PoseLibrarySourceHealth.Missing => "Folder missing",
        PoseLibrarySourceHealth.Denied => "Access denied",
        PoseLibrarySourceHealth.Invalid => "Invalid folder path",
        _ => "Folder could not be scanned",
    };

    private static void SourceError(Crystarium.FormScope form, string text, string detail)
    {
        var theme = Crystarium.ActiveTheme;
        ImGui.PushID("source-error");
        try
        {
            form.Custom(string.Empty, theme.Controls.FormRowHeight, row => Crystarium.TextInBand(
                row.Origin, new Vector2(row.Width, theme.Controls.FormRowHeight * row.Scale), text,
                new TextStyle { Size = theme.Typography.CaptionSize, Color = theme.Danger }), help: detail);
        }
        finally { ImGui.PopID(); }
    }

    private static void SourceParagraph(SettingsViewModel vm, Crystarium.FormScope form, string text)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = MathF.Max(1f, vm.LibraryContentWidth);
        var style = new TextStyle
            { Size = Crystarium.ActiveTheme.Typography.BodySize, Color = Crystarium.ActiveTheme.Text };
        // The shared text primitive allows long words to overflow. Break only
        // the display string so paths fit Settings; Copy path stays lossless.
        var display = new StringBuilder();
        foreach (var paragraph in text.Split('\n'))
            for (int start = 0; start < paragraph.Length;)
            {
                int low = 1, high = paragraph.Length - start;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (Crystarium.MeasureText(paragraph.Substring(start, middle), style).X <= width * scale)
                        low = middle;
                    else
                        high = middle - 1;
                }
                if (start + low < paragraph.Length && char.IsHighSurrogate(paragraph[start + low - 1]))
                    low = low > 1 ? low - 1 : Math.Min(2, paragraph.Length - start);
                display.Append(paragraph, start, low).Append('\n');
                start += low;
            }
        var rendered = display.ToString().TrimEnd('\n');
        var constraint = TextConstraint.Wrap(width, whitespace: TextWhitespace.PreWrap);
        float height = MathF.Max(Crystarium.MeasureText(rendered, style, constraint).Y / scale,
            Crystarium.ActiveTheme.Controls.FormRowHeight);
        ImGui.PushID(text);
        try
        {
            form.Custom(string.Empty, height, row => Crystarium.TextInBand(
                row.Origin, new Vector2(row.Width, height * scale), rendered, style, constraint, TextAlign.Start));
        }
        finally { ImGui.PopID(); }
    }

    private static void AddLibrarySource(SettingsViewModel vm)
    {
        string path = vm.LibraryNewPath.Trim();
        if (path.Length == 0)
            return;
        string name = vm.LibraryNewName.Trim();
        if (name.Length == 0)
        {
            try { name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')); }
            catch (ArgumentException) { name = path; }
        }
        vm.Library.Add(name.Length == 0 ? path : name, path);
        vm.LibraryNewName = vm.LibraryNewPath = string.Empty;
    }
}
