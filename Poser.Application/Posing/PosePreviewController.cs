using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Posing;

/// <summary>Shared baseline/rebase workflow for pose import and library previews.</summary>
public sealed class PosePreviewController
{
    private const string BaselineKey = "##preview-baseline";

    private const int BaselineRetryFrames = 60;

    // Callbacks own distinct slots: an old completion cannot overwrite a newer source.
    private sealed class Capture
    {
        public volatile PoseFile? Pose;
    }

    private readonly IPosePreviewRuntime _preview;
    private readonly IPoseFileCapture _poses;

    private ActorId? _source;
    private string? _path;

    private PoseImportOptions? _candidate;

    private (PosePreviewRequest First, PosePreviewRequest Second)? _sent;

    // ── the baseline ─────────────────────────────────────────────────────
    private PoseFile? _baseline;
    private PoseImportOptions? _baselineOptions;
    private Capture? _captured;
    private int _baselineArmedAt = -1000;

    public PosePreviewController(IPosePreviewRuntime preview, IPoseFileCapture poses)
    {
        _preview = preview;
        _poses = poses;
    }

    public bool IsOpen => _source is not null;

    public bool IsWaitingForBaseline => _source is not null && _baseline is null;

    /// <summary>Returns true when the caller must build options and call Pose. Frame is the UI clock.</summary>
    public bool Begin(ActorId source, string path, PoseImportOptions candidate, int frame)
    {
        if (source != _source)
        {
            // A different appearance means a different hidden body: the pose
            // standing on the old one says nothing about the new one. A
            // different actor also means a different pose to rebase onto.
            _source = source;
            InvalidateBaseline();
        }
        // Idempotent by contract, and restated every frame so a preview the
        // service dropped (a scene reload, a gpose exit) re-arms itself.
        _preview.Open(source);

        TakeCapture();
        if (_baseline is null)
        {
            // Nothing may be stated without one, and nothing may be REMEMBERED
            // either: the pose has to be stated the frame the capture lands.
            _path = null;
            _candidate = null;
            ArmBaseline(source, frame);
            return false;
        }

        if (NeedsRebuild(_path, _candidate, path, candidate))
        {
            _path = path;
            _candidate = candidate.Clone();
            return true;
        }

        // Close() (a gpose exit, a scene drop) forgets the pending pose; an
        // Open() alone re-arms only the body. Restate the sequence until the
        // service renders again — the service dedupes actual imports.
        if (!_preview.IsActive && _sent is { } cached)
            _preview.ShowSequence(cached.First, cached.Second);
        return false;
    }

    public static bool NeedsRebuild(
        string? shownPath,
        PoseImportOptions? shownCandidate,
        string path,
        PoseImportOptions candidate) =>
        !string.Equals(path, shownPath, StringComparison.Ordinal)
        || shownCandidate is null
        || !SameOptions(candidate, shownCandidate);

    public void Pose(string path, PoseImportOptions options)
    {
        if (_baseline is not { } baseline || _baselineOptions is not { } rebase)
            return;
        var sequence = (
            PosePreviewRequest.Memory(baseline, BaselineKey, rebase),
            PosePreviewRequest.File(path, options));
        _sent = sequence;
        _preview.ShowSequence(sequence.Item1, sequence.Item2);
    }

    public void InvalidateBaseline()
    {
        _baseline = null;
        _baselineOptions = null;
        _captured = null;
        _baselineArmedAt = -1000;
        _path = null;
        _candidate = null;
        _sent = null;
    }

    // Another surface owns the single native preview; forget our baseline without closing it.
    public void StandDown() => InvalidateBaseline();

    public void Close()
    {
        if (_source is null)
            return;
        _source = null;
        InvalidateBaseline();
        _preview.Close();
    }

    private void TakeCapture()
    {
        if (_captured is not { } capture)
            return;
        if (capture.Pose is not { } pose)
            return;
        _captured = null;
        _baseline = pose;
        _baselineOptions = BaselineOptions();
    }

    private void ArmBaseline(ActorId target, int frame)
    {
        if (frame - _baselineArmedAt < BaselineRetryFrames)
            return;
        _baselineArmedAt = frame;
        var capture = new Capture();
        _captured = capture;
        // Authored bones only: a full snapshot bakes whatever frame the
        // target's ANIMATION happened to be on — eyes caught mid-blink,
        // enforced forever on the preview body (user 2026-08-10: "eyes
        // unreliable"). The stance is the AUTHORED pose; everything else
        // rides the preview body's own live animation, exactly as it rides
        // the target's.
        var armed = _poses.CapturePoseFile(
            target,
            pose => capture.Pose = pose,
            authoredOnly: true);
        if (!armed.Success)
            _captured = null;
    }

    private static PoseImportOptions BaselineOptions() => new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyBody = true,
        ApplyFace = true,
        ApplyMainHand = true,
        ApplyOffHand = true,
        ApplyProp = true,
        ApplyOrnament = true,
        AsExpression = false,
        ResetBeforeImport = true,
        ApplyModelTransform = false,
        FreezeOnImport = false,
    };

    // Compare candidate options, not file-forced options. They describe different scopes.
    public static bool SameOptions(PoseImportOptions a, PoseImportOptions b) =>
        a.ApplyRotation == b.ApplyRotation
        && a.ApplyPosition == b.ApplyPosition
        && a.ApplyScale == b.ApplyScale
        && a.ApplyBody == b.ApplyBody
        && a.ApplyFace == b.ApplyFace
        && a.ApplyMainHand == b.ApplyMainHand
        && a.ApplyOffHand == b.ApplyOffHand
        && a.ApplyProp == b.ApplyProp
        && a.ApplyOrnament == b.ApplyOrnament
        && a.ApplyModelTransform == b.ApplyModelTransform
        && a.ResetBeforeImport == b.ResetBeforeImport
        && a.AsExpression == b.AsExpression
        && a.FilterIncludesDescendants == b.FilterIncludesDescendants
        && a.AnchorSelectedPositions == b.AnchorSelectedPositions
        && a.ExcludeUncategorizedBones == b.ExcludeUncategorizedBones
        && a.FreezeOnImport == b.FreezeOnImport
        && a.SuppressHistory == b.SuppressHistory
        && SameSet(a.ExcludedBonePrefixes, b.ExcludedBonePrefixes)
        && SameSet(a.BoneFilter, b.BoneFilter);

    private static bool SameSet<T>(ISet<T>? a, ISet<T>? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || a.Count != b.Count)
            return false;
        foreach (var item in a)
            if (!b.Contains(item))
                return false;
        return true;
    }

    public static PoseImportOptions Trim(PoseImportOptions options)
    {
        options.ApplyModelTransform = false;
        options.FreezeOnImport = false;
        return options;
    }
}
