using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// THE naming prompt: every "Save to library" and entity rename across the
/// UI asks through this one modal, wherever the button lives — the panes
/// had grown one-click saves that skipped it (ruled 2026-08-31: they ask).
/// One instance, drawn once per frame by the main window; opening replaces
/// whatever prompt was pending.
/// </summary>
public sealed class EntityNameModal
{
    private bool _open;
    private string _title = string.Empty;
    private string _value = string.Empty;
    private Action<string>? _apply;

    /// <summary>One text input between the two bars: header 44 + padded
    /// input row + footer 44.</summary>
    private const float NamePromptHeight = 152f;

    public void Open(string title, string current, Action<string> apply)
    {
        _title = title;
        _value = current;
        _apply = apply;
        _open = true;
    }

    public void Draw()
    {
        if (!_open || _apply is not { } apply)
            return;
        // Footer idiom, not body buttons: the footer bar right-aligns its
        // children, and the height fits one input with no dead band.
        Crystarium.Modal(
            "##name-entity",
            _open,
            next => _open = next,
            _title,
            height: NamePromptHeight,
            body: () => Crystarium.TextInput(
                "##name-entity-input", _value, next => _value = next),
            footer: () =>
            {
                // Enter is the blue button: the modal is one input, and
                // done is done.
                bool submit =
                    ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) ||
                    ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false);
                if (Crystarium.Button("Cancel", id: "name-entity-cancel"))
                    _open = false;
                ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
                if (Crystarium.Button(
                        "Save",
                        variant: ButtonVariant.Primary,
                        id: "name-entity-save") || submit)
                {
                    if (_value.Trim() is { Length: > 0 } trimmed)
                        apply(trimmed);
                    _open = false;
                }
            });
    }
}
