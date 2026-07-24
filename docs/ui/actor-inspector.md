# Actor inspector

Pose-stage actions use Crystarium's `compact` button variant (24 px high,
12 px label and horizontal padding, 5 px radius), matching the approved M11
rail/footer treatment. The larger 32 px Picto action style remains reserved for
forms and modals.

## Purpose

The actor inspector is the Pose rail shown when the primary sidebar selection is an `IActor`, rather than one of that actor's `IBone` children. It edits the transform of the complete game draw object and exposes actor-scope pose actions. Bone-local editing remains a separate selection state even though both states share `PoseInspectorPane` and `PoseRailPane` rendering code.

## Selection and routing

- `MainWindow.BuildSidebar` tags each root actor row with its `IActor` and every leaf bone row with its `IBone`.
- `MainWindow.OnRowClicked` sends plain, Ctrl, and Shift clicks to the matching `ISelectionService` operation and keeps the natural tab active.
- `PoseInspectorPane.SetEntity` receives `ISelectionService.Primary` once per frame.
- `PoseRailPane` branches its header actions using `PoseInspectorPane.IsActorSelection`. Actor selection shows **Reset transform** and **Mirror pose**; bone selection shows **Select children** and **Flip**.
- Every selected entity row is highlighted, while the primary actor or bone retains the semantic distinction that drives the inspector.

## Actor transform contract

Whole-actor position, rotation, and scale use `IPosingService`:

1. `GetEffectiveTransform` supplies either the current override or the live draw-object transform.
2. Dragging the rotation ball or an axis well calls `SetTransformOverride` for immediate feedback.
3. Releasing the drag records a `TransformActorAction`, or one `CompositeAction` for multiple selected actors, so undo and redo replay the complete gesture.
4. **Reset transform** calls `ClearTransformOverride` for every selected actor, restoring the transforms captured before their first edits.
5. Leaving GPose clears every override. Animation freeze/unfreeze does not clear or gate the model transform.

The last rule is intentional. Brio's `ModelPosingCapability.Transform` and Ktisis' actor `ITransform` target are model-space controls independent from skeleton animation playback. Animation can continue while the entire actor stays translated, rotated, or scaled because `PosingService` enforces the draw-object override.

The axis wells use the shared [precision transform input](precision-transform-input.md) contract: drag for continuous adjustment, wheel for stepped changes, and double-click for exact numeric entry. These interactions all feed the same actor or bone apply-and-history path.

The colored controls on the [rotation ball](rotation-ball.md) are also interactive:
red, green, and blue constrain the gesture to X, Y, and Z respectively.

When multiple actors or bones are selected, the primary value drives the shared [multi-selection transform](multi-selection-transforms.md) delta and the complete group is recorded as one undo step.

## Actor versus bone responsibilities

| Concern | Actor selection | Bone selection |
|---|---|---|
| Transform space | Whole draw object, model/world-facing | Parent-local values converted at the service boundary |
| Apply service | `IPosingService` | `IBonePosingService` |
| History action | `TransformActorAction` | `TransformBoneAction` |
| Header actions | Reset transform, Mirror pose | Select children, Flip |
| Animation warning | None; model transform is independent | Advisory warning while motion is live because game animation can rewrite bone positions |

## Expression action units

Actor selection exposes the complete per-race Ktisis action-unit catalog below the pose controls. The catalog is fixed and small, so it renders directly as 26 px padded rows inside the already-scrollable inspector; it does not add a second search field. Labels, slider tracks, and percentage values share one vertical center.

Each slider calls `IExpressionService.SetWeight`. The service recomputes a named `expression` delta layer and never clears the actor's interactive face-bone stacks. Reset removes only that layer. See [Expression service](../services/expression-service.md) and [Bone pose stacks](../architecture/pose-stacks.md).

This is intentionally separate from expression animation timelines: Poser does not implement Brio's animation surface, and these controls are pose deltas rather than a timeline player.

## Reference decisions

- **Brio backend:** `Brio/Brio/Capabilities/Posing/ModelPosingCapability.cs` owns an independent model-transform override; `ModelTransformService.cs` writes it to the draw object and intercepts game position resets.
- **Ktisis interaction:** `Ktisis/Interface/Windows/ObjectWindow.cs` resolves the selected actor as an `ITransformTarget` and edits it through the same transform table used for other transformable scene entities.
- **Anamnesis stability rule:** animation gating applies to unstable bone-position edits, not to moving the complete actor draw object.
- **Picto visual direction:** compact sidebar selection drives an adjacent inspector, so actor operations stay in that inspector instead of opening another window. DisplayFrame was consulted for the shared restrained visual language, but it has no actor-selection interaction to copy.

## Verification

Build verification covers the UI/service API boundary. Live scenarios confirm:

1. Click the actor root row (not a bone) and move each position, rotation, and scale axis.
2. Keep animation playing and confirm the actor remains at the override.
3. Freeze and resume animation and confirm the actor transform remains unchanged.
4. Undo and redo one drag.
5. Use **Reset transform** and confirm the actor returns to its pre-edit transform and the button disables.
6. Select a bone and confirm the header actions change back to **Select children** and **Flip**.
