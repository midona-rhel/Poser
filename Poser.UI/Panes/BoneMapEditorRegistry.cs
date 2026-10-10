using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Config;
using Poser.Documents.Config;

namespace Poser.UI;

/// <summary>Routes actor context actions to open editors without owning their windows.</summary>
public sealed class BoneMapEditorRegistry
{
    private readonly List<WeakReference<GraphicalBonePane>> _editors = new();

    internal void Register(GraphicalBonePane pane)
    {
        Remove(pane);
        _editors.Add(new(pane));
    }

    internal void Remove(GraphicalBonePane pane) =>
        _editors.RemoveAll(reference => !reference.TryGetTarget(out var editor) || ReferenceEquals(editor, pane));

    internal GraphicalBonePane? Find(ActorId actor, BoneMapKind kind, Guid? preset = null)
    {
        for (int i = _editors.Count - 1; i >= 0; i--)
        {
            if (!_editors[i].TryGetTarget(out var editor)) { _editors.RemoveAt(i); continue; }
            if (editor.EditingMap(actor, kind, preset)) return editor;
        }
        return null;
    }
}
