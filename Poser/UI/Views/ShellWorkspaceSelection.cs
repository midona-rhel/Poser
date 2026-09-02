using System;
using System.Collections.Generic;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI.Views;

/// <summary>What the workspace is showing. Exactly one of these is true at a
/// time, which is the whole point of the enum: two booleans could both be
/// set, and two booleans plus a selection could all three be set.</summary>
public enum ShellWorkspace
{
    /// <summary>The selected entity's own tab strip — the ordinary state.
    /// </summary>
    Entity,

    /// <summary>The pose library.</summary>
    Library,

    /// <summary>The whole scene: save, load, progress and recovery.</summary>
    Scene,
}

/// <summary>
/// The shell's ONE selection.
///
/// <para>The sidebar offers three kinds of target that all wear the same
/// selection pill: an entity row, the ENVIRONMENT header (which is an entity),
/// and the LIBRARY and SCENE headers (which are workspace MODES over the whole
/// scene rather than a thing inside it). A mode and an entity used to live on
/// separate tracks — a pair of booleans here, <see cref="SelectionSession"/>
/// there — so the tree could light SCENE and an actor at once, which is not a
/// selection the rest of the shell can answer: the title cell, the tab strip
/// and the inspector rail each have to pick one.</para>
///
/// <para>This type is the one place the two tracks meet. Entering a mode
/// releases the entity selection, and — through the live selection's own
/// change event — selecting ANY entity leaves the mode, whichever surface did
/// the selecting: a sidebar row, an overlay handle, a world adoption, a spawn.
/// No call site has to remember, which is exactly why the old rule (stated
/// only at the two openers) held for the library and was missed for the
/// scene.</para>
///
/// <para>Leaving a mode RESTATES NOTHING beyond raising <see cref="Left"/>:
/// leaving is never the last thing a caller does — it selects next, or opens
/// the other mode — and a tab/layout resync here would resolve against the
/// selection mid-change. The host resyncs once, after every change it is
/// going to make.</para>
/// </summary>
public sealed class ShellWorkspaceSelection : IDisposable
{
    private readonly SelectionSession _selection;
    private readonly Action<IReadOnlyList<SelectionId>> _selectionChanged;
    private ShellWorkspace _workspace = ShellWorkspace.Entity;
    private bool _disposed;

    public ShellWorkspaceSelection(SelectionSession selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
        _selectionChanged = OnSelectionChanged;
        _selection.SelectionChanged += _selectionChanged;
    }

    /// <summary>Raised with the mode that was just left, so the host can tell
    /// that mode's pane it is no longer shown. Never raised for
    /// <see cref="ShellWorkspace.Entity"/>, which is not a mode to leave.
    /// </summary>
    public event Action<ShellWorkspace>? Left;

    public ShellWorkspace Workspace => _workspace;

    public bool IsScene => _workspace == ShellWorkspace.Scene;

    /// <summary>
    /// Enters a workspace mode: leaves whatever mode was showing and releases
    /// the entity selection, because the two are alternatives.
    ///
    /// <para>Openers only — a second request must not toggle a workspace the
    /// user is already looking at — so entering the mode that is already
    /// showing answers false and touches nothing, the selection included.
    /// </para>
    /// </summary>
    /// <returns>Whether the workspace actually moved.</returns>
    public bool Enter(ShellWorkspace workspace)
    {
        if (workspace == ShellWorkspace.Entity)
            return Leave();
        if (_workspace == workspace)
            return false;

        Leave();
        _workspace = workspace;
        // The mode IS the selection now. Clear() on an already-empty selection
        // publishes nothing, so this cannot re-enter through the change event;
        // a non-empty one publishes an EMPTY list, which the handler ignores.
        _selection.Clear();
        return true;
    }

    /// <summary>Leaves any workspace mode without touching the selection.
    /// </summary>
    /// <returns>Whether a mode was actually left.</returns>
    public bool Leave()
    {
        if (_workspace == ShellWorkspace.Entity)
            return false;
        var left = _workspace;
        _workspace = ShellWorkspace.Entity;
        Left?.Invoke(left);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _selection.SelectionChanged -= _selectionChanged;
    }

    /// <summary>The net under every selecting surface: a live selection that
    /// names something is an entity selection, and an entity selection is not
    /// a workspace mode.</summary>
    private void OnSelectionChanged(IReadOnlyList<SelectionId> selected)
    {
        if (selected.Count > 0)
            Leave();
    }
}
