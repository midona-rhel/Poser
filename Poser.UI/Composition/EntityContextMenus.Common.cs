using System;
using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.UI;

/// <summary>The actions every kind of thing shares. Each is written once here, with
/// its name, icon, place in the menu and what it does, and every menu uses it.</summary>
internal sealed partial class EntityContextMenus
{
    private ContextMenuItem HandleItem(SelectionId target) => HandleItem(_overlayPresentation.IsHandleShown(target),
        () => _overlayPresentation.ToggleHandle(target), "Only the overlay handle; does not hide the entity");

    private ContextMenuItem HandleItem(Guid group) => HandleItem(_overlayPresentation.IsHandleShown(group),
        () => _overlayPresentation.ToggleHandle(group), "Only the overlay handle; does not hide the group or its members");

    private static ContextMenuItem HandleItem(bool shown, Action toggle, string help) =>
        new(shown ? "Hide handle" : "Show handle", shown ? TablerIcon.EyeOff : TablerIcon.Eye, keepOpen: true, help: help)
        { OnInvoke = toggle, Place = MenuPlace.Handle };

    private static ContextMenuItem VisibilityItem(bool visible, Action toggle) =>
        new(visible ? "Hide" : "Show", visible ? TablerIcon.EyeOff : TablerIcon.Eye)
        { OnInvoke = toggle, Place = MenuPlace.Visibility };

    private static ContextMenuItem PlaybackItem(bool playing, Action toggle, bool disabled = false) =>
        new(playing ? "Pause" : "Play", playing ? TablerIcon.PlayerPause : TablerIcon.PlayerPlay, disabled: disabled)
        { OnInvoke = toggle, Place = MenuPlace.Playback };

    private static ContextMenuItem LockItem(bool locked, Action toggle) =>
        new(locked ? "Unlock" : "Lock", locked ? TablerIcon.LockOpen : TablerIcon.Lock)
        { OnInvoke = toggle, Place = MenuPlace.Lock };

    private ContextMenuItem RenameItem(string title, string current, Action<string> apply, bool disabled = false,
        Action? clear = null, string clearHelp = "") =>
        new("Rename", TablerIcon.Edit, disabled: disabled)
        { OnInvoke = () => _names.Open(title, current, apply, clear: clear, clearHelp: clearHelp), Place = MenuPlace.Rename };

    /// <summary>Duplicate. With a posed copy on offer it opens a choice of a plain
    /// copy or a copy in the current pose.</summary>
    private static ContextMenuItem DuplicateItem(Action plain, Action? withPose = null, bool posable = true, bool disabled = false) =>
        withPose is { } posed
            ? new("Duplicate", TablerIcon.Copy, disabled: disabled, submenuItems:
              [
                  new("Duplicate", TablerIcon.Copy) { OnInvoke = plain },
                  new("Duplicate with pose", TablerIcon.Stack2, disabled: !posable) { OnInvoke = posed },
              ]) { Place = MenuPlace.Duplicate }
            : new("Duplicate", TablerIcon.Copy, disabled: disabled) { OnInvoke = plain, Place = MenuPlace.Duplicate };

    private ContextMenuItem DuplicateItem(SelectionId id) => DuplicateItem(() => DuplicateAndSelect(id));

    private ContextMenuItem SaveToLibraryItem(SelectionId id, string noun, string name, bool disabled = false) =>
        new("Save to library", TablerIcon.Library, disabled: disabled)
        { OnInvoke = () => _librarySave.Ask(id, noun, name), Place = MenuPlace.More };

    private ContextMenuItem SaveGroupToLibraryItem(IReadOnlyList<SelectionId> members, string name) =>
        new("Save to library", TablerIcon.Library)
        { OnInvoke = () => _librarySave.AskGroup(members, name), Place = MenuPlace.More };

    private static ContextMenuItem SaveToFileItem(Action open) =>
        new("Save to file…", TablerIcon.DeviceFloppy) { OnInvoke = open, Place = MenuPlace.More };

    private ContextMenuItem MoveToCameraItem(IReadOnlyList<SelectionId> targets, bool disabled = false) =>
        new("Move to camera", TablerIcon.Camera, disabled: disabled)
        { OnInvoke = () => MoveToCamera(targets), Place = MenuPlace.Own };

    private static ContextMenuItem DestroyItem(Action remove) =>
        new("Destroy", TablerIcon.Trash, danger: true) { OnInvoke = remove, Place = MenuPlace.Lifetime };

    /// <summary>Destroys one thing Poser made, and deselects it.</summary>
    private ContextMenuItem DestroyItem(SelectionId id) =>
        DestroyItem(() => _ = RemoveEntitiesSafely([id], () => _selection.Remove(id)));

    /// <summary>Gives a borrowed thing back to the game, where it was.</summary>
    private ContextMenuItem ReleaseItem(SelectionId id) =>
        new("Release", TablerIcon.X) { OnInvoke = () => _ = RemoveEntitiesSafely([id], () => _selection.Remove(id)), Place = MenuPlace.Lifetime };

    private static ContextMenuItem DestroyAllItem(string things, bool disabled, Action confirm) =>
        new($"Destroy all {things}…", TablerIcon.Trash, danger: true, disabled: disabled)
        { OnInvoke = confirm, Place = MenuPlace.More };

    /// <summary>A submenu whose rows each carry their own action.</summary>
    private static ContextMenuItem Submenu(string label, TablerIcon icon, MenuPlace place, ContextMenuItem[] rows,
        bool disabled = false, string? help = null) =>
        new(label, icon, disabled: disabled, help: help, submenuItems: rows) { Place = place };
}
