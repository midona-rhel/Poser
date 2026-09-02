using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>The one name prompt: a title, one input, the confirm in the
/// footer. Rules ride along when a caller has them — a validator whose
/// problem line is reserved under the input and disables the confirm, a
/// sanitizer for the typed text, and a Clear verb in place of Cancel for a
/// name that can be unset.</summary>
public sealed class EntityNameModal
{
    private bool _open;
    private string _title = string.Empty;
    private string _value = string.Empty;
    private string _confirm = "Save";
    private Action<string>? _apply;
    private Action? _clear;
    private string _clearHelp = string.Empty;
    private Func<string, string?>? _validate;
    private Func<string, string>? _sanitize;
    private string? _placeholder;

    /// <summary>Header 44 + padded input row + footer 44.</summary>
    private const float NamePromptHeight = 152f;
    /// <summary>The reserved problem line under the input.</summary>
    private const float ProblemLineHeight = 24f;

    public void Open(
        string title,
        string current,
        Action<string> apply,
        string confirm = "Save",
        Action? clear = null,
        string clearHelp = "",
        Func<string, string?>? validate = null,
        Func<string, string>? sanitize = null,
        string? placeholder = null)
    {
        _title = title;
        _value = current;
        _apply = apply;
        _confirm = confirm;
        _clear = clear;
        _clearHelp = clearHelp;
        _validate = validate;
        _sanitize = sanitize;
        _placeholder = placeholder;
        _open = true;
    }

    public void Draw()
    {
        if (!_open || _apply is not { } apply)
            return;
        string? problem = _validate?.Invoke(_value);
        float height = NamePromptHeight + (_validate is null ? 0f : ProblemLineHeight);
        // Footer idiom, not body buttons: the footer bar right-aligns its
        // children, and the height fits one input with no dead band.
        Crystarium.Modal(
            "##name-entity",
            _open,
            next => _open = next,
            _title,
            height: height,
            body: () =>
            {
                Crystarium.TextInput(
                    "##name-entity-input", _value,
                    next => _value = _sanitize is { } sanitize ? sanitize(next) : next,
                    placeholder: _placeholder);
                if (_validate is null)
                    return;
                // The problem line is reserved whether or not there is one,
                // so the footer never moves while typing.
                var theme = Crystarium.ActiveTheme;
                float scale = ImGuiHelpers.GlobalScale;
                ImGui.Dummy(new Vector2(0f, 4f * scale));
                Crystarium.TextAt(
                    ImGui.GetCursorScreenPos(), problem ?? string.Empty,
                    new TextStyle
                    {
                        Size = theme.Typography.CaptionSize,
                        Color = theme.FormHint,
                    });
                ImGui.Dummy(new Vector2(1f, (theme.Typography.CaptionSize + 4f) * scale));
            },
            footer: () =>
            {
                // Enter is the blue button: the modal is one input, and
                // done is done.
                bool submit = problem is null && (
                    ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) ||
                    ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));
                if (_clear is { } clear)
                {
                    if (Crystarium.Button("Clear", id: "name-entity-clear", help: _clearHelp))
                    {
                        clear();
                        _open = false;
                    }
                }
                else if (Crystarium.Button("Cancel", id: "name-entity-cancel"))
                    _open = false;
                ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
                if (Crystarium.Button(
                        _confirm,
                        variant: ButtonVariant.Primary,
                        disabled: problem is not null,
                        help: problem,
                        id: "name-entity-save") || submit)
                {
                    if (_value.Trim() is { Length: > 0 } trimmed)
                        apply(trimmed);
                    _open = false;
                }
            });
    }
}
