using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Posing;
using Poser.Game.Preview;

namespace Poser.UI;

/// <summary>
/// The shared drive of <see cref="PosePreviewService"/>. There is exactly ONE
/// CharaView, so every surface that shows a pose preview — the library rail,
/// the import dialog's preview column — states the same three things through
/// one binder: whose appearance the hidden body borrows, which file it holds,
/// and which options that file lands with.
///
/// <para>The compare is Ktisis' <c>PreviewNode.NeedsUpdate</c>: the preview
/// re-poses when the import options move under it, not only when the file
/// does. It is split in two calls rather than taking a build callback because
/// the CANDIDATE is polled every frame while the real build may read the file:
/// <see cref="Begin"/> answers whether that read is worth making.</para>
///
/// <para>REBASE THEN APPLY. The preview must show what the CONFIRM will do,
/// and a confirm lands the file on the target actor as it stands right now —
/// the "Reset first" checkbox is off by default, so most imports LAYER. A
/// preview body that had been wiped clean before every re-pose could never
/// show that (user 2026-08-10: "the preview doesn't match the final apply"),
/// and a narrower re-pose left the previous, wider one's bones behind, so
/// swapping options drifted. So each re-pose is TWO stages: the target's own
/// captured pose with a full-scope reset, and then the file with the user's
/// real options verbatim. The body therefore stands exactly where the target
/// stands before the file touches it — layering and scope shrink both read
/// true — and the service runs the pair as one supersedable request.</para>
/// </summary>
internal sealed class PosePreviewBinder
{
    /// <summary>What the baseline stage is deduped on in the service, in place
    /// of a path. Constant: a fresh capture always brings a fresh options
    /// instance with it, which is the other half of that compare.</summary>
    private const string BaselineKey = "##preview-baseline";

    /// <summary>Frames between capture attempts once one has been made. A
    /// capture is armed through the export pipeline and can be refused (another
    /// capture in flight); retrying every frame would log a warning per frame.
    /// The initial arm is immediate — <see cref="_baselineArmedAt"/> starts far
    /// enough behind any real frame count to clear this outright.</summary>
    private const int BaselineRetryFrames = 60;

    /// <summary>A capture handed back by the framework thread, tagged with the
    /// drive session it was armed for. ONE reference write, picked up on the
    /// draw thread — every other field here belongs to the draw thread
    /// alone.</summary>
    private sealed record Capture(int Generation, PoseFile? Pose);

    private readonly PosePreviewService _preview;
    private readonly CleanPoseFacade _poses;

    /// <summary>The actor the preview is borrowing an appearance from — and
    /// whose current pose is the rebase baseline: both call sites drive the
    /// preview off the actor an apply would land on. The path it was last told
    /// to show is stated ONCE per change; the service re-renders on its own,
    /// and re-importing every frame would be an import per frame.</summary>
    private IActor? _source;
    private string? _path;

    /// <summary>The UI-derived build the last sent options were made from —
    /// what a fresh candidate is compared against. Never the sent instance
    /// itself: a build forced by the FILE says things no candidate ever
    /// will.</summary>
    private PoseImportOptions? _candidate;

    /// <summary>The pair handed to the service, restated while it warms up so
    /// a re-parse never happens on the way.</summary>
    private (PosePreviewRequest First, PosePreviewRequest Second)? _sent;

    // ── the baseline ─────────────────────────────────────────────────────
    private PoseFile? _baseline;
    private PoseImportOptions? _baselineOptions;
    private volatile Capture? _captured;
    private int _baselineGeneration;
    private int _baselineArmedAt = -1000;

    public PosePreviewBinder(PosePreviewService preview, CleanPoseFacade poses)
    {
        _preview = preview;
        _poses = poses;
    }

    /// <summary>Whether this binder has a preview open to give back.</summary>
    public bool IsOpen => _source is not null;

    /// <summary>Whether the drive is waiting on its baseline capture — the
    /// frames between opening and the target's pose coming back, during which
    /// nothing may be stated: a pose shown against no baseline is the mismatch
    /// this binder exists to remove.</summary>
    public bool IsWaitingForBaseline => _source is not null && _baseline is null;

    /// <summary>
    /// The frame's statement of what the preview should show. Returns true when
    /// the caller must follow with <see cref="Pose"/> — the file or the options
    /// moved — and false when the standing pose still holds, in which case the
    /// restate a warming service needs is made here.
    /// </summary>
    public bool Begin(IActor source, string path, PoseImportOptions candidate)
    {
        if (!ReferenceEquals(source, _source))
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
            ArmBaseline(source);
            return false;
        }

        if (!string.Equals(path, _path, StringComparison.Ordinal)
            || _candidate is null
            || !SameOptions(candidate, _candidate))
        {
            _path = path;
            _candidate = candidate;
            return true;
        }

        // Close() (a gpose exit, a scene drop) forgets the pending pose; an
        // Open() alone re-arms only the body. Restate the sequence until the
        // service renders again — the service dedupes actual imports.
        if (!_preview.IsActive && _sent is { } cached)
            _preview.ShowSequence(cached.First, cached.Second);
        return false;
    }

    /// <summary>The real build, after <see cref="Begin"/> asked for one: the
    /// target's own stance, then this file on top of it.</summary>
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

    /// <summary>
    /// Drops the captured stance: the target has been posed under this drive
    /// (an apply the hosting surface made), or the drive session itself is
    /// starting over. The next <see cref="Begin"/> captures again and states
    /// nothing until it lands — a stale baseline would rebase onto a pose the
    /// actor left.
    /// </summary>
    public void InvalidateBaseline()
    {
        _baseline = null;
        _baselineOptions = null;
        _captured = null;
        _baselineGeneration++;
        _baselineArmedAt = -1000;
        _path = null;
        _candidate = null;
        _sent = null;
    }

    /// <summary>Forgets what was stated WITHOUT touching the service: another
    /// surface has taken the seat and is driving it, and whatever it shows is
    /// not this binder's pose to remember. The baseline goes with it — the
    /// other surface is free to pose the target while it holds the seat.
    /// </summary>
    public void StandDown() => InvalidateBaseline();

    /// <summary>Gives the seat back. Idempotent — the frame after a close must
    /// not close again.</summary>
    public void Close()
    {
        if (_source is null)
            return;
        _source = null;
        InvalidateBaseline();
        _preview.Close();
    }

    /// <summary>The capture the framework thread left, if it belongs to the
    /// session standing now. A capture that came back empty is left to the
    /// retry window rather than treated as a baseline of nothing.</summary>
    private void TakeCapture()
    {
        if (_captured is not { } capture)
            return;
        _captured = null;
        if (capture.Generation != _baselineGeneration || capture.Pose is null)
            return;
        _baseline = capture.Pose;
        _baselineOptions = BaselineOptions();
    }

    /// <summary>
    /// Arms the target's own pose as a file. Asynchronous by nature — the
    /// export pipeline hands the pose back only once the apply pass has made
    /// every raw transform cache current (see
    /// <see cref="CleanPoseFacade.CapturePoseFile"/>) — so the callback tags
    /// itself with the session and the draw thread decides whether it still
    /// matters.
    /// </summary>
    private void ArmBaseline(IActor target)
    {
        int frame = ImGui.GetFrameCount();
        if (frame - _baselineArmedAt < BaselineRetryFrames)
            return;
        _baselineArmedAt = frame;
        int generation = ++_baselineGeneration;
        // Authored bones only: a full snapshot bakes whatever frame the
        // target's ANIMATION happened to be on — eyes caught mid-blink,
        // enforced forever on the preview body (user 2026-08-10: "eyes
        // unreliable"). The stance is the AUTHORED pose; everything else
        // rides the preview body's own live animation, exactly as it rides
        // the target's.
        var armed = _poses.CapturePoseFile(
            target,
            pose => _captured = new Capture(generation, pose),
            authoredOnly: true);
        if (!armed.Success)
            _captured = new Capture(generation, null);
    }

    /// <summary>
    /// What the baseline stage lands with: EVERYTHING, reset first. The stage
    /// exists to make the preview body stand exactly where the target stands,
    /// so nothing may be scoped out of it — the scope-shrink residue that made
    /// option swapping drift is precisely what a narrower reset leaves behind.
    /// It moves no actor and freezes nothing, like every preview import.
    /// </summary>
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

    /// <summary>
    /// Whether two UI-derived option builds say the same thing — the preview's
    /// NeedsUpdate. BOTH sides must come from the same file-free build: a
    /// routing forced by the FILE moves fields no checkbox did, so comparing a
    /// candidate against a file-forced instance would either mask a toggle
    /// (excluding the field) or re-import every frame (keeping it). Comparing
    /// like with like needs no exclusions, so every field is compared, the two
    /// sets by content since each build makes new ones.
    /// </summary>
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
        && a.ExcludeUncategorizedBones == b.ExcludeUncategorizedBones
        && a.FreezeOnImport == b.FreezeOnImport
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

    /// <summary>The preview's own trim of a build: everything that would move
    /// the preview ACTOR is taken back out — a preview is a POSE, never a
    /// placement, and it must never leave a body frozen. RESET IS NOT TRIMMED:
    /// it is the user's own statement about how the file lands, and the
    /// baseline stage is what gives the preview a body to state it against.
    /// </summary>
    public static PoseImportOptions Trim(PoseImportOptions options)
    {
        options.ApplyModelTransform = false;
        options.FreezeOnImport = false;
        return options;
    }
}
