# Brio and Ktisis posing reference

## Why both are consulted

Brio is the native-behavior reference. Ktisis is the interaction reference.
Poser copies neither codebase's project or window hierarchy.

## Live posing model: follow Brio

Brio's skeleton update path calls the game's original animation/physics update
before applying stored posing transforms. It refreshes transform caches,
reparents partial skeletons, refreshes again, and observes the final result.
Editing therefore does not require animation to be frozen.

Poser preserves that order:

1. game animation and physics produce the current native baseline;
2. persistent authored layers are evaluated;
3. hierarchy and constraints are resolved;
4. the result is written on the framework/native update path;
5. the final state is recorded for diagnostics.

Freeze is an editing convenience, never authorization to write a bone.
Character, weapon, prop, and ornament skeleton slots ultimately need stable
bindings; the current migration only proves the character slot and must not be
called complete parity.

Relevant local Brio references:

- `../Brio/Brio/Game/Posing/SkeletonService.cs`
- `../Brio/Brio/Capabilities/Posing/SkeletonPosingCapability.cs`

## Freeze model: do not follow Ktisis

Ktisis suppresses several native model-space, kine-driver, position, and
animation paths while posing and reports animation as frozen. That produces a
coherent editor, but it is not Poser's desired live-composition behavior.

Relevant local Ktisis reference:

- `../Ktisis/Ktisis/Editor/Posing/PosingModule.cs`

## Interaction ideas retained from Ktisis

Poser adopts the useful separation between scene state, selection, transform
handling, property projection, and scene-tree rendering. It does not adopt the
large independently managed window collection.

## Known native risks

- object addresses can be reused after redraw;
- skeleton generations invalidate active gestures;
- partial skeletons require correct parent/cache ordering;
- animation can overwrite an edit unless layers are reapplied after the native
  baseline;
- all native access must remain on the framework thread;
- runtime pose state keyed only by address is transitional and must move to
  stable actor/bone ids.
