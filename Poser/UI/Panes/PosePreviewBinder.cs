using System;
using System.Collections.Generic;
using Poser.Entities;
using Poser.Files;
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
/// </summary>
internal sealed class PosePreviewBinder
{
    private readonly PosePreviewService _preview;

    /// <summary>The actor the preview is borrowing an appearance from, and the
    /// path it was last told to show. The pose is stated ONCE per change — the
    /// service re-renders on its own, and re-importing every frame would be an
    /// import per frame.</summary>
    private IActor? _source;
    private string? _path;

    /// <summary>The UI-derived build the last sent options were made from —
    /// what a fresh candidate is compared against. Never the sent instance
    /// itself: a build forced by the FILE says things no candidate ever
    /// will.</summary>
    private PoseImportOptions? _candidate;

    /// <summary>The instance handed to the service, restated while it warms
    /// up so a re-parse never happens on the way.</summary>
    private PoseImportOptions? _sent;

    public PosePreviewBinder(PosePreviewService preview) => _preview = preview;

    /// <summary>Whether this binder has a preview open to give back.</summary>
    public bool IsOpen => _source is not null;

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
            // standing on the old one says nothing about the new one.
            _source = source;
            _path = null;
            _candidate = null;
        }
        // Idempotent by contract, and restated every frame so a preview the
        // service dropped (a scene reload, a gpose exit) re-arms itself.
        _preview.Open(source);

        if (!string.Equals(path, _path, StringComparison.Ordinal)
            || _candidate is null
            || !SameOptions(candidate, _candidate))
        {
            _path = path;
            _candidate = candidate;
            return true;
        }

        // Close() (a gpose exit, a scene drop) forgets the pending pose; an
        // Open() alone re-arms only the body. Restate the pose until the
        // service renders again — the service dedupes actual imports.
        if (!_preview.IsActive && _sent is { } cached)
            _preview.ShowPose(path, cached);
        return false;
    }

    /// <summary>The real build, after <see cref="Begin"/> asked for one.</summary>
    public void Pose(string path, PoseImportOptions options)
    {
        _sent = options;
        _preview.ShowPose(path, options);
    }

    /// <summary>Forgets what was stated WITHOUT touching the service: another
    /// surface has taken the seat and is driving it, and whatever it shows is
    /// not this binder's pose to remember.</summary>
    public void StandDown()
    {
        _path = null;
        _candidate = null;
        _sent = null;
    }

    /// <summary>Gives the seat back. Idempotent — the frame after a close must
    /// not close again.</summary>
    public void Close()
    {
        if (_source is null)
            return;
        _source = null;
        StandDown();
        _preview.Close();
    }

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
    /// placement, and it must never leave a body frozen.</summary>
    public static PoseImportOptions Trim(PoseImportOptions options)
    {
        options.ResetBeforeImport = true;
        options.ApplyModelTransform = false;
        options.FreezeOnImport = false;
        return options;
    }
}
