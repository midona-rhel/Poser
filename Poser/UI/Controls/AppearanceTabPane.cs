using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.Data.Config;
using Poser.Entities;
using Poser.IPC;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for appearance management using Penumbra, Glamourer, and CustomizePlus.
/// </summary>
public class AppearanceTabPane : ITabPane
{
    private readonly IPenumbraService? _penumbraService;
    private readonly IGlamourerService? _glamourerService;
    private readonly ICustomizePlusService? _customizePlusService;

    // Cached data
    private Dictionary<Guid, string>? _collections;
    private Dictionary<Guid, string>? _designs;
    private IReadOnlyList<(Guid Id, string Name)>? _profiles;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(5);

    // Selection state
    private int _selectedDesignIndex;

    // Current entity context
    private IEntity? _entity;

    public string Name => "Appearance";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.User;
    public bool IsEnabled => _entity is IActor && HasAnyService;

    private bool HasAnyService =>
        (_penumbraService?.IsAvailable ?? false) ||
        (_glamourerService?.IsAvailable ?? false) ||
        (_customizePlusService?.IsAvailable ?? false);

    public AppearanceTabPane(
        IPenumbraService? penumbraService,
        IGlamourerService? glamourerService,
        ICustomizePlusService? customizePlusService)
    {
        _penumbraService = penumbraService;
        _glamourerService = glamourerService;
        _customizePlusService = customizePlusService;
    }

    public void SetEntity(IEntity? entity)
    {
        _entity = entity;

        // Reset selection when entity changes
        _selectedDesignIndex = 0;
    }

    public void Draw()
    {
        if (_entity is not IActor actor)
        {
            ImGui.TextDisabled("Select an actor to manage appearance");
            return;
        }

        RefreshCacheIfNeeded();

        // Penumbra section
        if (_penumbraService != null)
        {
            DrawPenumbraSection(actor);
        }

        // Glamourer section
        if (_glamourerService != null)
        {
            DrawGlamourerSection(actor);
        }

        // CustomizePlus section
        if (_customizePlusService != null)
        {
            DrawCustomizePlusSection(actor);
        }

        // Show message if no services available
        if (!HasAnyService)
        {
            ImGui.TextDisabled("No appearance plugins detected");
            ImGui.TextDisabled("Install Penumbra, Glamourer, or CustomizePlus");
        }
    }

    private void DrawPenumbraSection(IActor actor)
    {
        var isAvailable = _penumbraService!.IsAvailable;

        DrawSectionHeader("Penumbra", isFirst: true);

        if (!isAvailable)
        {
            ImGui.TextDisabled(GetStatusText(_penumbraService.Status));
            return;
        }

        var collections = _collections ?? new Dictionary<Guid, string>();
        var collectionList = collections.ToList();

        // Get current collection
        var currentCollection = _penumbraService.GetCollectionForActor(actor);
        var currentIndex = currentCollection.HasValue
            ? collectionList.FindIndex(c => c.Key == currentCollection.Value) + 1
            : 0;

        // Create display list with "None" option
        var displayNames = new[] { "(None)" }.Concat(collectionList.Select(c => c.Value)).ToArray();

        using (var row = PoserUI.Row(PoserUI.DropdownHeight))
        {
            row.Label("Collection", 80);
            if (row.DropdownFill("##penumbra_collection", ref currentIndex, displayNames))
            {
                if (currentIndex == 0)
                {
                    // Selected None - clear collection
                    // Note: Penumbra doesn't have a "clear" API, so we'd need to use default
                }
                else
                {
                    var selectedCollection = collectionList[currentIndex - 1];
                    _penumbraService.SetCollectionForActor(actor, selectedCollection.Key);
                }
            }
        }

        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("", 80);
            if (row.Button("##penumbra_redraw", "Redraw"))
            {
                _penumbraService.RedrawActor(actor);
            }
        }
    }

    private void DrawGlamourerSection(IActor actor)
    {
        var isAvailable = _glamourerService!.IsAvailable;

        DrawSectionHeader("Glamourer");

        if (!isAvailable)
        {
            ImGui.TextDisabled(GetStatusText(_glamourerService.Status));
            return;
        }

        var designs = _designs ?? new Dictionary<Guid, string>();
        var designList = designs.ToList();

        // Create display list with "None" option
        var displayNames = new[] { "(Select Design)" }.Concat(designList.Select(d => d.Value)).ToArray();

        using (var row = PoserUI.Row(PoserUI.DropdownHeight))
        {
            row.Label("Design", 80);
            if (row.DropdownFill("##glamourer_design", ref _selectedDesignIndex, displayNames))
            {
                if (_selectedDesignIndex > 0)
                {
                    var selectedDesign = designList[_selectedDesignIndex - 1];
                    _glamourerService.ApplyDesign(actor, selectedDesign.Key);
                }
            }
        }

        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("", 80);
            if (row.Button("##glamourer_revert", "Revert"))
            {
                _glamourerService.RevertAppearance(actor);
                _selectedDesignIndex = 0;
            }
        }
    }

    private void DrawCustomizePlusSection(IActor actor)
    {
        var isAvailable = _customizePlusService!.IsAvailable;

        DrawSectionHeader("CustomizePlus");

        if (!isAvailable)
        {
            ImGui.TextDisabled(GetStatusText(_customizePlusService.Status));
            return;
        }

        var profiles = _profiles ?? Array.Empty<(Guid Id, string Name)>();

        // Get current profile
        var currentProfile = _customizePlusService.GetActiveProfile(actor);
        var currentIndex = currentProfile.HasValue
            ? profiles.ToList().FindIndex(p => p.Id == currentProfile.Value) + 1
            : 0;

        // Create display list with "None" option
        var displayNames = new[] { "(None)" }.Concat(profiles.Select(p => p.Name)).ToArray();

        using (var row = PoserUI.Row(PoserUI.DropdownHeight))
        {
            row.Label("Profile", 80);
            if (row.DropdownFill("##customizeplus_profile", ref currentIndex, displayNames))
            {
                if (currentIndex == 0)
                {
                    _customizePlusService.ClearProfile(actor);
                }
                else
                {
                    var selectedProfile = profiles[currentIndex - 1];
                    _customizePlusService.SetProfile(actor, selectedProfile.Id);
                }
            }
        }

        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("", 80);
            if (row.Button("##customizeplus_clear", "Clear"))
            {
                _customizePlusService.ClearProfile(actor);
            }
        }
    }

    private static void DrawSectionHeader(string text, bool isFirst = false)
    {
        if (!isFirst)
            PoserUI.Separator();

        using (var row = Flex.Row())
        {
            row.Fill((w, h) =>
            {
                float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                ImGui.TextColored(UIColors.TextDisabled, text);
            });
        }
    }

    private void RefreshCacheIfNeeded()
    {
        if (DateTime.Now - _lastCacheTime < CacheInterval)
            return;

        _lastCacheTime = DateTime.Now;

        if (_penumbraService?.IsAvailable == true)
        {
            _collections = _penumbraService.GetCollections();
        }

        if (_glamourerService?.IsAvailable == true)
        {
            _designs = _glamourerService.GetDesigns();
        }

        if (_customizePlusService?.IsAvailable == true)
        {
            _profiles = _customizePlusService.GetProfiles();
        }
    }

    private static string GetStatusText(IPCStatus status) => status switch
    {
        IPCStatus.NotInstalled => "Plugin not installed",
        IPCStatus.VersionMismatch => "Version incompatible",
        IPCStatus.Disabled => "Disabled in settings",
        IPCStatus.Error => "Error connecting",
        _ => "Not available"
    };
}
