using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Application.Operations;
using Poser.Files;
using Poser.Game.Scene;
using Poser.Library;

namespace Poser.UI;

/// <summary>
/// The whole-shot workspace: the ONE surface that saves a shot, loads one,
/// states what a running load is doing, and states what a finished one left
/// behind — every named refusal and every recovered file.
///
/// <para>It composes existing idioms rather than restating them: the two
/// destinations are <see cref="Crystarium.FileDialog"/>s exactly as the light
/// and camera files are, the load dialog's side panel is the codec's own
/// verdict on the highlighted file (so a corrupt or future scene says so
/// before it is opened, not after), and the page is
/// <see cref="Crystarium.Page"/> sections throughout.</para>
///
/// <para>The pane owns no scene state. Progress, the terminal receipt and the
/// snapshot result are read from their owners' immutable read models every
/// frame.</para>
/// </summary>
public sealed class ScenePane
{
    /// <summary>How many recent shots the page lists. The list is a shortcut
    /// to the last few, not a browser — the browser is the load dialog.</summary>
    private const int RecentShotCount = 8;

    private readonly SceneWorkflow _workflow;
    private readonly SceneAutoSaveService _snapshots;
    private readonly IPoseLibraryService _library;

    private readonly Crystarium.FileDialog _saveBrowser =
        new("Save Shot", new[] { SceneFile.Extension }, isSaveMode: true);
    private readonly Crystarium.FileDialog _loadBrowser =
        new("Load Shot", new[] { SceneFile.Extension });
    private readonly Crystarium.FileDialog _snapshotBrowser =
        new("Load Snapshot", new[] { SceneFile.Extension });

    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private string _description = string.Empty;
    private string _note = string.Empty;

    /// <summary>The load dialog's verdict on whichever row the list is
    /// highlighting, re-probed only when that path changes: the probe reads
    /// and validates a whole document, so it must not run per frame.</summary>
    private string? _probedPath;
    private SceneMetadataReadOutcome? _probed;

    public ScenePane(
        SceneWorkflow workflow,
        SceneAutoSaveService snapshots,
        IPoseLibraryService library)
    {
        _workflow = workflow;
        _snapshots = snapshots;
        _library = library;

        var verdict = new FileSidePanel(220f, DrawVerdictPanel);
        _loadBrowser.SidePanels.Add(verdict);
        _snapshotBrowser.SidePanels.Add(verdict);
    }

    /// <summary>Pumped every frame by the window: a dialog must survive the
    /// frames in which this pane's mode is not the one being drawn.</summary>
    public void DrawBrowsers()
    {
        _saveBrowser.Draw();
        _loadBrowser.Draw();
        _snapshotBrowser.Draw();
    }

    /// <summary>Refreshes the library scan when the shot workspace is opened:
    /// the recent list is read from the shared snapshot, and a shot saved
    /// since the last pass is exactly what the user is looking for.</summary>
    public void OnShown() => _library.RequestScan();

    public void Draw(Vector2 origin, Vector2 size)
    {
        var progress = _workflow.Progress;
        var receipt = _workflow.Receipt;
        bool busy = _workflow.Busy;

        Crystarium.Page("scene-shot", origin, size, page =>
        {
            page.Section("Shot", form =>
            {
                form.TextInput(
                    "Description",
                    _description,
                    value => _description = value,
                    placeholder: "What this shot is",
                    disabled: busy,
                    help: "Saved into the file and shown beside it in every listing.");
                form.Actions(
                    string.Empty,
                    actions =>
                    {
                        actions.Button(
                            "Save the shot…",
                            OpenSave,
                            disabled: busy,
                            help: busy
                                ? "A scene operation is already running."
                                : "Capture every actor, prop, light, camera and the environment into one file.",
                            variant: ButtonVariant.Primary);
                        actions.Button(
                            "Load a shot…",
                            OpenLoad,
                            disabled: busy,
                            help: busy
                                ? "A scene operation is already running."
                                : "Validate a whole shot file, then restore it into this session.");
                    },
                    fullWidth: true);
                form.Status(_note);
            },
            divider: false);

            if (busy && progress is { } running)
                DrawProgress(page, running);

            if (!busy && progress?.Outcome is { } outcome)
                DrawOutcome(page, outcome, receipt);

            DrawRecent(page, busy);
            DrawSnapshots(page, busy);
        });
    }

    // ── progress ─────────────────────────────────────────────────────────

    private void DrawProgress(Crystarium.PageScope page, SceneProgress progress)
    {
        page.Section("In progress", form =>
        {
            float fraction = progress.EntitiesTotal > 0
                ? Math.Clamp(
                    progress.EntitiesDone / (float)progress.EntitiesTotal, 0f, 1f)
                : 0f;
            string readout = progress.EntitiesTotal > 0
                ? $"{progress.EntitiesDone}/{progress.EntitiesTotal}"
                : "—";
            form.Progress(
                PhaseLabel(progress.Phase),
                fraction,
                readout,
                cancel: () => _workflow.Cancel(),
                cancelDisabled: !progress.Cancellable,
                cancelHelp: progress.Cancellable
                    ? "Stop and undo everything this load has created."
                    : "This phase can no longer be cancelled.");
            form.Status(
                $"{(progress.Kind == SceneOperationKind.Save ? "Saving" : "Loading")} " +
                $"{progress.FileName}.");
        });
    }

    /// <summary>The phase vocabulary in the user's words. One case per
    /// <see cref="ScenePhase"/>, so a new phase cannot slip through unnamed.
    /// </summary>
    private static string PhaseLabel(ScenePhase phase) => phase switch
    {
        ScenePhase.Capturing => "Capturing the shot",
        ScenePhase.Writing => "Writing the file",
        ScenePhase.Reading => "Reading and validating the file",
        ScenePhase.SpawningEntities => "Spawning actors and props",
        ScenePhase.AwaitingActors => "Waiting for the actors to build",
        ScenePhase.ApplyingRelationships => "Attaching companions",
        ScenePhase.ApplyingPose => "Applying poses",
        ScenePhase.ApplyingPresentation => "Applying visibility",
        ScenePhase.ApplyingCameras => "Restoring cameras",
        ScenePhase.ApplyingLights => "Restoring lights",
        ScenePhase.ApplyingEnvironment => "Restoring the environment",
        ScenePhase.Committing => "Finishing",
        ScenePhase.RollingBack => "Undoing what was created",
        ScenePhase.Completed => "Done",
        ScenePhase.RolledBack => "Undone",
        ScenePhase.Failed => "Failed",
        ScenePhase.Cancelled => "Cancelled",
        _ => "Working",
    };

    // ── terminal result ──────────────────────────────────────────────────

    private void DrawOutcome(
        Crystarium.PageScope page,
        SceneOutcome outcome,
        OperationReceipt? receipt)
    {
        var refusals = outcome.Entities.Where(entity => !entity.Restored).ToList();
        page.Section("Last result", form =>
        {
            form.ReadOnly(
                "Outcome",
                StateLabel(outcome.State),
                unavailable: !outcome.Success,
                help: receipt is null
                    ? null
                    : $"Operation {receipt.OperationId:D}, epoch {receipt.OperationEpoch}.");
            form.Status(outcome.Detail);

            // Named refusals beside restored entities: this is the partial
            // recovery, so every one of them is a row rather than a count.
            foreach (var refusal in refusals)
            {
                form.ReadOnly(
                    refusal.Kind,
                    $"{refusal.Name} — {refusal.Detail ?? "refused"}",
                    unavailable: true);
            }

            foreach (var note in outcome.Notes)
                form.Status(note);

            // Bytes that survived an uncertain commit. They are named so the
            // user can recover them by hand; nothing here deletes them.
            foreach (var evidence in outcome.RecoveryEvidencePaths)
            {
                form.ReadOnlyWithActions(
                    "Recovered file",
                    Path.GetFileName(evidence),
                    actions => actions.Button(
                        "Open folder",
                        () => OpenFolder(Path.GetDirectoryName(evidence)),
                        help: evidence),
                    help: evidence,
                    unavailable: true);
            }

            if (outcome.LeftEntitiesBehind && refusals.Count > 0)
            {
                form.Status(
                    "Everything that did restore was kept. Remove what you do " +
                    "not want, or load the shot again.");
            }
        });
    }

    private static string StateLabel(OperationReceiptState state) => state switch
    {
        OperationReceiptState.Applied => "Applied",
        OperationReceiptState.RolledBack => "Rolled back — nothing was left behind",
        OperationReceiptState.Cancelled => "Cancelled — nothing was left behind",
        OperationReceiptState.Failed => "Failed",
        _ => state.ToString(),
    };

    // ── recent shots ─────────────────────────────────────────────────────

    private void DrawRecent(Crystarium.PageScope page, bool busy)
    {
        var recent = _library.Snapshot.Entries
            .Where(entry => entry.Kind == PoseLibraryEntryKind.Scene)
            .OrderByDescending(entry => entry.Modified)
            .Take(RecentShotCount)
            .ToList();

        page.Section("Recent shots", form =>
        {
            if (recent.Count == 0)
            {
                form.Status(
                    "No shots in the pose library folders yet. A shot saved " +
                    "into one shows up here.");
                return;
            }

            foreach (var entry in recent)
            {
                bool valid = entry.MetadataStatus == PoseLibraryMetadataStatus.Valid;
                string value = valid
                    ? entry.SceneContents
                    : StatusWord(entry.MetadataStatus);
                form.ReadOnlyWithActions(
                    entry.Name,
                    $"{value} · {entry.ModifiedText}",
                    actions => actions.Button(
                        "Load",
                        () => BeginLoad(entry.FilePath),
                        disabled: busy || !valid,
                        help: valid
                            ? entry.FilePath
                            : entry.MetadataDetail),
                    help: valid ? entry.FilePath : entry.MetadataDetail,
                    unavailable: !valid);
            }
        });
    }

    /// <summary>A listing's one-word verdict. A file the codec refuses is
    /// LISTED and named — never hidden, so a user can see that the file they
    /// remember is the one that went bad.</summary>
    private static string StatusWord(PoseLibraryMetadataStatus status) =>
        status switch
        {
            PoseLibraryMetadataStatus.Future => "Saved by a newer Poser",
            PoseLibraryMetadataStatus.Oversized => "Too large to read",
            PoseLibraryMetadataStatus.Corrupt => "Cannot be read",
            _ => string.Empty,
        };

    // ── automatic snapshots ──────────────────────────────────────────────

    private void DrawSnapshots(Crystarium.PageScope page, bool busy)
    {
        var last = _snapshots.LastResult;
        page.Section("Automatic snapshots", form =>
        {
            form.ReadOnly(
                "Last snapshot",
                last.Status switch
                {
                    SceneAutoSaveStatus.Written => "Taken",
                    SceneAutoSaveStatus.Skipped => "Skipped",
                    SceneAutoSaveStatus.Failed => "Failed",
                    SceneAutoSaveStatus.RecoveryRequired => "Needs recovery",
                    _ => "None yet",
                },
                unavailable: last.Status is SceneAutoSaveStatus.Failed
                    or SceneAutoSaveStatus.RecoveryRequired,
                help: last.Path);
            form.Status(last.Detail);

            foreach (var evidence in last.Evidence)
            {
                form.ReadOnlyWithActions(
                    "Recovered file",
                    Path.GetFileName(evidence),
                    actions => actions.Button(
                        "Open folder",
                        () => OpenFolder(Path.GetDirectoryName(evidence)),
                        help: evidence),
                    help: evidence,
                    unavailable: true);
            }

            form.Actions(
                string.Empty,
                actions => actions.Button(
                    "Load a snapshot…",
                    OpenSnapshots,
                    disabled: busy || !Directory.Exists(_snapshots.RootDirectory),
                    help: Directory.Exists(_snapshots.RootDirectory)
                        ? _snapshots.RootDirectory
                        : "No automatic snapshot has been taken yet."),
                fullWidth: true);
        });
    }

    // ── the load dialog's verdict column ─────────────────────────────────

    /// <summary>
    /// The highlighted file, read through the SAME codec the load uses. A
    /// listing can therefore never offer a shot the load would reject without
    /// saying so first. The probe is cached on the path because it validates a
    /// whole bounded document.
    /// </summary>
    private void DrawVerdictPanel(Vector2 min, Vector2 max, string? path)
    {
        if (path is null)
        {
            _probedPath = null;
            _probed = null;
            return;
        }
        if (!string.Equals(path, _probedPath, StringComparison.Ordinal))
        {
            _probedPath = path;
            _probed = SceneFileStore.Default.ReadMetadata(path);
        }
        if (_probed is not { } metadata)
            return;

        var theme = Crystarium.ActiveTheme;
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = theme.Spacing.Four * scale;
        float width = MathF.Max(0f, max.X - min.X - inset * 2f);
        var cursor = new Vector2(min.X + inset, min.Y + inset);
        float line = theme.Controls.FormRowHeight * scale;

        void Line(string text, Vector4 color, float size)
        {
            Crystarium.TextInBand(
                cursor,
                new Vector2(width, line),
                text,
                new TextStyle { Size = size, Color = color },
                TextConstraint.Truncate(width),
                TextAlign.Start,
                besideIcon: true);
            cursor.Y += line;
        }

        bool valid = metadata.Status == SceneEntryStatus.Valid;
        Line(
            valid ? "Valid shot" : StatusWordFor(metadata.Status),
            valid ? theme.Text : theme.TextDim,
            theme.Typography.LabelSize);

        if (!valid)
        {
            Line(
                metadata.Failure?.Detail ?? "The shot could not be read.",
                theme.FormHint,
                theme.Typography.CaptionSize);
            return;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Description))
            Line(metadata.Description!, theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.ActorCount} actors", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.PropCount} props", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.LightCount} lights", theme.FormHint, theme.Typography.CaptionSize);
        Line($"{metadata.CameraCount} cameras", theme.FormHint, theme.Typography.CaptionSize);
        if (metadata.SavedAt is { } saved)
        {
            Line(
                saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                theme.FormHint,
                theme.Typography.CaptionSize);
        }
    }

    private static string StatusWordFor(SceneEntryStatus status) => status switch
    {
        SceneEntryStatus.Future => "Saved by a newer Poser",
        SceneEntryStatus.Oversized => "Too large to read",
        _ => "Cannot be read",
    };

    // ── actions ──────────────────────────────────────────────────────────

    private void OpenSave() => _saveBrowser.Open(_lastPath, path =>
    {
        _lastPath = Path.GetDirectoryName(path) ?? _lastPath;
        if (!path.EndsWith(SceneFile.Extension, StringComparison.OrdinalIgnoreCase))
            path += SceneFile.Extension;
        var started = _workflow.BeginSave(
            path,
            string.IsNullOrWhiteSpace(_description) ? null : _description);
        _note = started.Success ? string.Empty : started.Detail ?? string.Empty;
        if (started.Success)
            _library.RequestScan();
    });

    private void OpenLoad() => _loadBrowser.Open(_lastPath, path =>
    {
        _lastPath = Path.GetDirectoryName(path) ?? _lastPath;
        BeginLoad(path);
    });

    private void OpenSnapshots() =>
        _snapshotBrowser.Open(_snapshots.RootDirectory, BeginLoad);

    private void BeginLoad(string path)
    {
        var started = _workflow.BeginLoad(path);
        _note = started.Success ? string.Empty : started.Detail ?? string.Empty;
    }

    private void OpenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            _note = $"The folder could not be opened: {ex.Message}";
        }
    }
}
