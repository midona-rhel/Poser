# Category Entity

**Interface:** `ICategory`
**Implementation:** `Category` (to be created)
**Location:** `Poser/Entities/Category.cs` (to be created)

## Purpose

Represents a logical grouping of bones that can be selected and transformed together.

## Interface (Proposed)

```csharp
public interface ICategory : IEntity
{
    /// <summary>
    /// Unique identifier for this category.
    /// </summary>
    string CategoryId { get; }

    /// <summary>
    /// The skeleton this category belongs to.
    /// </summary>
    ISkeleton Skeleton { get; }

    /// <summary>
    /// All bones in this category.
    /// </summary>
    IReadOnlyList<IBone> Bones { get; }

    /// <summary>
    /// Child categories.
    /// </summary>
    IReadOnlyList<ICategory> SubCategories { get; }
}
```

## Why Category is an Entity

Currently, categories are handled specially in EditorState:
- `_selectedCategory` - string ID
- `_selectedCategorySkeleton` - skeleton reference
- `GetSelectedCategoryBones()` - returns bones

Making Category an `IEntity` provides:
1. **Unified selection** - SelectionService handles categories like any entity
2. **Properties panel** - Can show category info naturally
3. **Hierarchy display** - Categories appear in entity tree
4. **Event consistency** - SelectionChangedEvent works for categories

## Category Configuration

Categories are defined in `CategoryConfig.json`:

```json
{
  "categories": [
    {
      "id": "body",
      "name": "Body",
      "children": [
        {
          "id": "spine",
          "name": "Spine",
          "bones": ["j_sebo_a", "j_sebo_b", "j_sebo_c"]
        },
        {
          "id": "arms",
          "name": "Arms",
          "children": [
            {
              "id": "left_arm",
              "name": "Left Arm",
              "bones": ["j_ude_a_l", "j_ude_b_l", "j_te_l"]
            }
          ]
        }
      ]
    }
  ]
}
```

## Category Hierarchy

```
Root Categories
├── Body
│   ├── Spine (bones: j_sebo_a, j_sebo_b, j_sebo_c)
│   ├── Arms
│   │   ├── Left Arm (bones: j_ude_a_l, j_ude_b_l, j_te_l)
│   │   └── Right Arm (bones: j_ude_a_r, j_ude_b_r, j_te_r)
│   └── Legs
│       ├── Left Leg
│       └── Right Leg
├── Head
│   ├── Face
│   └── Hair
└── Equipment
    ├── Main Hand
    └── Off Hand
```

## Transform Behavior

When a category is transformed:
1. Calculate delta from gizmo
2. Apply delta to ALL bones in category
3. Create composite history action

```csharp
// In TransformService
case ICategory category:
    var actions = new List<IHistoryAction>();
    foreach (var bone in category.Bones)
    {
        var old = GetTransform(bone);
        ApplyToBone(bone, delta, components);
        var newT = GetTransform(bone);
        actions.Add(new TransformBoneAction(bone, old, newT));
    }
    _historyService.Push(new CompositeAction($"Transform {category.Name}", actions));
    break;
```

## Properties Panel Display

When a Category is the primary selection:
- **Header**: Category name
- **Info**: Number of bones in category
- **Transform**: "Use gizmo to transform category bones"

No transform sliders - categories use gizmo only for practical reasons.

## Gizmo Behavior

For categories:
- **Position**: Average of all bone positions
- **Rotation**: First bone's rotation (for orientation)
- **Pivot modes** apply as with multi-bone selection

## Current Implementation

Currently categories are NOT entities. Selection is handled specially:

```csharp
// EditorState
public void SelectCategory(string categoryId, ISkeleton skeleton)
{
    ClearSelection();  // Clear entity selection
    _selectedCategory = categoryId;
    _selectedCategorySkeleton = skeleton;
}

public IReadOnlyList<IBone> GetSelectedCategoryBones()
{
    // Look up category config, find bones
}
```

## Migration Path

To make categories proper entities:
1. Create `ICategory` interface
2. Create `Category` class implementing `IEntity`
3. Build category entities when skeleton is created
4. Add categories as children of skeleton in hierarchy
5. Update SelectionService to handle categories
6. Remove special category handling from EditorState
