using System;
using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.UI.Composition;

/// <summary>A created entity that is selected once the scene refresh has
/// bound it: armed at creation, reconciled each frame until its id
/// resolves. A creation that dies before it binds is forgotten.</summary>
public sealed class PendingSelection<T> where T : class
{
    private T? _pending;

    public void Arm(T created) => _pending = created;

    public void Reconcile(
        Func<T, SelectionId?> bind,
        SelectionSession selection,
        Func<T, bool>? stillValid = null)
    {
        if (_pending is not { } pending)
            return;
        if (stillValid is not null && !stillValid(pending))
        {
            _pending = null;
            return;
        }
        if (bind(pending) is not { } id)
            return;
        selection.Select(id);
        _pending = null;
    }
}
