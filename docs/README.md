# Poser documentation

Durable contracts only, stated once. Git history is the archive; source and
XML comments carry implementation detail.

| Concept | Normative home |
|---|---|
| Product scope, layers, startup | [architecture/product-and-boundaries.md](architecture/product-and-boundaries.md) |
| Posing runtime (native boundary) | [architecture/posing-runtime.md](architecture/posing-runtime.md) |
| Scene/selection/gesture/history state | [architecture/application-state.md](architecture/application-state.md) |
| Retained UI workspace | [architecture/ui-workspace.md](architecture/ui-workspace.md) |
| Selection + transform interaction | [features/selection-and-transforms.md](features/selection-and-transforms.md) |
| Pose operations (mirror/flip/reset/stash) | [features/pose-operations.md](features/pose-operations.md) |
| Expression, gaze, IK | [features/expression-gaze-and-ik.md](features/expression-gaze-and-ik.md) |
| Animation playback and blending | [features/animation.md](features/animation.md) |
| Pose files and transfer | [features/files-and-transfer.md](features/files-and-transfer.md) |
| Runtime appearance and Glamourer handoff | [features/runtime-appearance.md](features/runtime-appearance.md) |
| Implementation/review loop | [process/external-implementation-review-loop.md](process/external-implementation-review-loop.md) |
| Live testing gate | [process/testing.md](process/testing.md) |
| Dependency updates | [process/dependency-currency.md](process/dependency-currency.md) |
| Backlog | `backlog/PBI-*.md` |

Rules: one normative home per contract — link, never restate. New documents
only for durable concepts with non-obvious invariants (see `AGENTS.md`).
Brio/Ktisis references only where they explain an intentional compatibility
decision. Delete superseded prose; do not archive it.
