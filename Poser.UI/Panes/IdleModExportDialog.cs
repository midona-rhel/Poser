using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Identity;

namespace Poser.UI;

public sealed class IdleModExportDialog(IIdleModExport export, UserNotices notices, IFramework framework)
{
    private readonly string ModalId = $"##idle-export-{Guid.NewGuid():N}";
    private const string ModalTitle = "Export Idle Pose";
    private readonly Crystarium.FileDialog _dialog = new("Save and Export Idle Pose", new[] { ".pmp" }, isSaveMode: true)
        { ConfirmLabel = "Save and Export" };
    private readonly HashSet<int> _races = [];
    private IdleModChoices? _choices;
    private ActorId _actor;
    private Controls.RememberedFolder? _folder;
    private string _name = string.Empty;
    private IdleModOptions? _pendingOptions;
    private int _slot = 1;
    private bool _open, _loading, _choosePath, _choosingPath;
    public bool Busy => export.Busy || _loading;

    public async void Open(ActorId actor, Controls.RememberedFolder folder)
    {
        if (Busy) return;
        _loading = true;
        try
        {
            var choices = await export.DescribeAsync(actor);
            await framework.RunOnFrameworkThread(() =>
            {
                _actor = actor;
                _folder = folder;
                _choices = choices;
                _name = choices.Name;
                _races.Clear();
                _races.Add(choices.SourceRaceSexId);
                _slot = 1;
                _open = true;
            });
        }
        catch (Exception ex) { await framework.RunOnFrameworkThread(() => notices.Failed("Idle export", ex.Message)); }
        finally { _loading = false; }
    }

    public void Draw()
    {
        // Open the browser after the options modal has relinquished its surface.
        if (_choosePath && _folder != null)
        {
            _choosePath = false;
            _choosingPath = true;
            var actor = _actor;
            var options = _pendingOptions!;
            _folder.Open(_dialog, path =>
            {
                _choosingPath = false;
                _ = Save(actor, path, options);
            });
        }
        _dialog.Draw();
        if (_choosingPath && !_dialog.IsOpen) { _choosingPath = false; _open = true; }
        if (!_open || _choices is not { } choices) return;
        var slots = Enumerable.Range(1, 6).Where(s => _races.Count > 0 && choices.Targets
            .Where(t => _races.Contains(t.RaceSexId)).All(t => t.Slots.Contains(s))).ToArray();
        if (!slots.Contains(_slot)) _slot = slots.FirstOrDefault();
        string? problem = string.IsNullOrWhiteSpace(_name) || _name.Length > 128 ? "Enter a mod name (up to 128 characters)."
            : _races.Count == 0 ? "Select a race and gender."
            : slots.Length == 0 ? "No shared standing pose slot is available." : null;
        Crystarium.Dialog(ModalId, _open, value => _open = value, ModalTitle,
            size: DialogSize.Medium, height: 650f,
            body: () => Crystarium.Page("idle-export-options", ImGui.GetCursorScreenPos(), ImGui.GetContentRegionAvail(), page =>
            {
                page.Section("Mod", form =>
                {
                    form.TextInput("Name", _name, next => _name = next);
                    form.Dropdown("Replaces", slots.Select(s => $"Standing pose {s} (/cpose)").ToArray(),
                        Math.Max(0, Array.IndexOf(slots, _slot)), next => { if (next >= 0 && next < slots.Length) _slot = slots[next]; },
                        disabled: slots.Length == 0);
                }, divider: false, allowDisclosure: false);
                page.Section("Race and gender", form =>
                {
                    for (int i = 0; i < choices.Targets.Length; i += 2)
                    {
                        var male = choices.Targets[i];
                        var female = choices.Targets[i + 1];
                        form.Checkboxes(male.Label[..^5], Item(male, "Male"), Item(female, "Female"));
                    }
                    Crystarium.CheckItem Item(IdleModTarget target, string label) => new(label,
                        _races.Contains(target.RaceSexId), value =>
                        { if (value) _races.Add(target.RaceSexId); else _races.Remove(target.RaceSexId); },
                        target.RaceSexId == choices.SourceRaceSexId ? "Source actor's race and gender; preserves the captured pose and skeleton."
                            : "Experimental retarget to standard player faces; proportions and contacts may differ.");
                }, allowDisclosure: false);
            }, labelColumnWidth: 125f),
            footer: () =>
            {
                if (Crystarium.Button("Cancel", id: "idle-export-cancel")) CloseOptions();
                ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
                if (Crystarium.Button("Save and Export", variant: ButtonVariant.Primary, disabled: problem != null || Busy,
                    help: problem, id: "idle-export-save"))
                {
                    _pendingOptions = new IdleModOptions(_name.Trim(), _slot, _races.Order().ToImmutableArray());
                    CloseOptions();
                    _choosePath = true;
                }
            });
    }

    private void CloseOptions()
    {
        _open = false;
    }

    private async Task Save(ActorId actor, string path, IdleModOptions options)
    {
        try
        {
            await export.ExportAsync(actor, path, options);
            await framework.RunOnFrameworkThread(() => notices.Done($"Idle mod saved to {path}. Import the PMP in Penumbra."));
        }
        catch (Exception ex)
        {
            await framework.RunOnFrameworkThread(() => { notices.Failed("Idle export", ex.Message); _open = true; });
        }
    }
}
