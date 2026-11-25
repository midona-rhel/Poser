using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>Save to library, for every kind of thing and from every place it is
/// offered: asks for the entry's name, starting from the thing's own name, then
/// saves it.</summary>
public sealed class LibrarySavePrompt(ScenePane scene, EntityNameModal names)
{
    /// <summary>Saves one thing. <paramref name="noun"/> names it in the
    /// prompt's title, as in "Save light to library".</summary>
    public void Ask(SelectionId target, string noun, string name) =>
        names.Open($"Save {noun} to library", name, entry => scene.SaveEntry(target, entry));

    /// <summary>Saves a group with all its members.</summary>
    public void AskGroup(IReadOnlyList<SelectionId> members, string name) =>
        names.Open("Save group to library", name, entry => scene.SaveGroupEntry(members, entry));
}
