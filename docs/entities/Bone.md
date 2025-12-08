# Bone Entity

**Interface:** `IBone`
**Implementation:** `Bone`
**Location:** `Poser/Entities/Bone.cs`

## Purpose

Represents a single bone in an actor's skeleton that can be transformed.

## Interface

```csharp
public interface IBone : IEntity, ITransformable
{
    /// <summary>
    /// Internal game bone name (e.g., "j_kosi", "j_ude_a_l").
    /// </summary>
    string BoneName { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Bone index within the partial skeleton.
    /// </summary>
    int BoneIndex { get; }

    /// <summary>
    /// Partial skeleton ID (0=body, 1=equipment, etc.)
    /// </summary>
    int PartialId { get; }

    /// <summary>
    /// Parent bone in hierarchy.
    /// </summary>
    IBone? ParentBone { get; }

    /// <summary>
    /// Child bones in hierarchy.
    /// </summary>
    IReadOnlyList<IBone> ChildBones { get; }

    /// <summary>
    /// Whether this is a partial skeleton root (typically hidden).
    /// </summary>
    bool IsPartialRoot { get; }

    /// <summary>
    /// Whether this is the main skeleton root.
    /// </summary>
    bool IsSkeletonRoot { get; }

    /// <summary>
    /// Whether bone should be hidden in UI (partial roots).
    /// </summary>
    bool IsHiddenBone { get; }

    /// <summary>
    /// Last known transform from game memory.
    /// </summary>
    Transform LastTransform { get; }

    /// <summary>
    /// The skeleton this bone belongs to.
    /// </summary>
    ISkeleton Skeleton { get; }
}
```

## Capabilities

IBone implements only `ITransformable`:
- Can be positioned (translation)
- Can be rotated
- Can be scaled (limited support)

## Bone Naming Convention

FFXIV uses Japanese bone naming:

| Prefix | Meaning |
|--------|---------|
| `j_` | Joint |
| `n_` | Node (attachment point) |
| `_l` | Left side |
| `_r` | Right side |
| `_a`, `_b`, `_c` | Chain segment (a=upper, b=middle, c=lower) |

Common bones:
- `j_kosi` - Waist/pelvis
- `j_sebo_a/b/c` - Spine segments
- `j_kubi` - Neck
- `j_kao` - Face
- `j_ude_a_l` - Left upper arm
- `j_te_l` - Left hand

## Partial Skeletons

Bones belong to partial skeletons:

| Partial ID | Contents |
|------------|----------|
| 0 | Main body skeleton |
| 1 | Equipment (hair, hats) |
| 2 | Main hand weapon |
| 3 | Off hand weapon |
| 4+ | Additional equipment |

**Note:** Partial roots are hidden in UI but exist in the hierarchy.

## Bone Lookup

```csharp
// By name (may be ambiguous)
var spine = skeleton.GetBone("j_sebo_a");

// By partial + index (unique)
var bone = skeleton.GetBone(partialId: 0, boneIndex: 5);
```

**Warning:** Some bone names appear in multiple partials. Use `GetBone(int, int)` for precise lookup.

## Transform

Bone transforms are stored as modifications (deltas from original):

```csharp
// Get current modification
var mod = _bonePosingService.GetModification(bone);

// Apply delta transform
_bonePosingService.ApplyTransform(bone, delta);

// Set absolute modification
_bonePosingService.SetModification(bone, newMod);
```

The `LastTransform` property reflects the current game state (updated each frame by hooks).

## Hierarchy

Bones form a tree hierarchy:

```
n_root
└── j_kosi (waist)
    ├── j_sebo_a (spine lower)
    │   └── j_sebo_b (spine middle)
    │       └── j_sebo_c (spine upper)
    │           ├── j_kubi (neck)
    │           │   └── j_kao (head/face)
    │           ├── j_ude_a_l (left upper arm)
    │           │   └── j_ude_b_l (left lower arm)
    │           └── j_ude_a_r (right upper arm)
    ├── j_asi_a_l (left upper leg)
    │   └── j_asi_b_l (left lower leg)
    └── j_asi_a_r (right upper leg)
```

## Properties Panel Display

When a Bone is the primary selection:
- **Transform display**: Current position, rotation (read-only display)
- **Gizmo manipulation**: User transforms via gizmo, not property sliders

Bones don't show animation/gaze tabs (only actors have those capabilities).

## Selection Behavior

Selecting a bone:
1. Auto-enters posing mode (if not already)
2. Freezes parent actor's animation
3. Locks parent actor's gaze
4. Shows gizmo at bone position
