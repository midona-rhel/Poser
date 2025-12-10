using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Core;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Single source of truth for entity selection.
/// UI components call methods directly; service publishes result events.
/// </summary>
public class SelectionService : ISelectionService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly List<IEntity> _selected = new();

    // Track last clicked entity for shift-select range
    private IEntity? _lastClicked;

    public SelectionService(IEventBus eventBus)
    {
        _eventBus = eventBus;

        // Clear selection when exiting GPose
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public IReadOnlyList<IEntity> Selected => _selected.AsReadOnly();

    public IEntity? Primary => _selected.Count > 0 ? _selected[0] : null;

    /// <summary>
    /// Gets the last clicked entity for shift-select range operations.
    /// </summary>
    public IEntity? LastClicked => _lastClicked;

    public void Select(IEntity entity)
    {
        // Clear previous selection state
        foreach (var e in _selected)
        {
            e.IsSelected = false;
        }
        _selected.Clear();

        // Select new entity
        _selected.Add(entity);
        entity.IsSelected = true;
        _lastClicked = entity;

        PublishSelectionChanged();
    }

    public void AddToSelection(IEntity entity)
    {
        if (_selected.Contains(entity))
            return;

        _selected.Add(entity);
        entity.IsSelected = true;
        _lastClicked = entity;

        PublishSelectionChanged();
    }

    public void RemoveFromSelection(IEntity entity)
    {
        if (_selected.Remove(entity))
        {
            entity.IsSelected = false;
            PublishSelectionChanged();
        }
    }

    public void ToggleSelection(IEntity entity)
    {
        if (_selected.Contains(entity))
        {
            RemoveFromSelection(entity);
        }
        else
        {
            AddToSelection(entity);
        }
    }

    public void SelectRange(IEntity from, IEntity to, IEnumerable<IEntity>? displayOrder)
    {
        if (displayOrder == null)
        {
            // Fallback: just select both entities
            AddToSelection(from);
            AddToSelection(to);
            return;
        }

        var orderedList = displayOrder.ToList();
        var fromIndex = orderedList.IndexOf(from);
        var toIndex = orderedList.IndexOf(to);

        if (fromIndex < 0 || toIndex < 0)
        {
            // Fallback: just select both
            if (!_selected.Contains(from))
            {
                _selected.Add(from);
                from.IsSelected = true;
            }
            if (!_selected.Contains(to))
            {
                _selected.Add(to);
                to.IsSelected = true;
            }
        }
        else
        {
            // Select range in display order
            var start = Math.Min(fromIndex, toIndex);
            var end = Math.Max(fromIndex, toIndex);

            for (var i = start; i <= end; i++)
            {
                var entity = orderedList[i];
                if (!_selected.Contains(entity))
                {
                    _selected.Add(entity);
                    entity.IsSelected = true;
                }
            }
        }

        PublishSelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var e in _selected)
        {
            e.IsSelected = false;
        }
        _selected.Clear();

        PublishSelectionChanged();
    }

    public bool IsSelected(IEntity entity) => _selected.Contains(entity);

    public IEnumerable<T> GetSelected<T>() where T : IEntity
    {
        return _selected.OfType<T>();
    }

    public T? GetFirstSelected<T>() where T : class, IEntity
    {
        return _selected.OfType<T>().FirstOrDefault();
    }

    private void PublishSelectionChanged()
    {
        // Publish the main selection event with all selected entities
        _eventBus.Publish(new SelectionChangedEvent(_selected.ToList()));

        // Also publish bone selection for backwards compatibility
        var selectedBone = _selected.OfType<IBone>().FirstOrDefault();
        _eventBus.Publish(new BoneSelectionChangedEvent(selectedBone));
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            ClearSelection();
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        GC.SuppressFinalize(this);
    }
}
