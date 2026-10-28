using System;
using System.Collections.Generic;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI.Views;

/// <summary>The mutually exclusive workspace displayed by the shell.</summary>
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
/// Coordinates workspace mode and entity selection.
///
/// <para>Library and Scene are workspace modes; ordinary rows and Environment
/// use entity selection. These states are mutually exclusive.</para>
///
/// <para>Entering a mode clears entity selection. Selecting any entity leaves
/// the active mode through the selection change event.</para>
///
/// <para>Leaving raises <see cref="Left"/> without resynchronizing layout; the
/// host does that after the complete selection change.</para>
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
    /// showing returns false without changing the entity selection.
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
        // The workspace mode replaces entity selection. Clear() on an already-empty selection
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
