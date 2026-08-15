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
| Scenes (save/restore) | [features/scenes.md](features/scenes.md) |
| Runtime appearance and Glamourer handoff | [features/runtime-appearance.md](features/runtime-appearance.md) |
| Implementation/review loop | [process/external-implementation-review-loop.md](process/external-implementation-review-loop.md) |
| Live testing gate | [process/testing.md](process/testing.md) |
| Dependency updates | [process/dependency-currency.md](process/dependency-currency.md) |
| Licensing, attribution, dependency verdicts | [../THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md) |
| Public-release procedure | [release/runbook.md](release/runbook.md) |
| What must not ship publicly | [release/exclusions.md](release/exclusions.md) |
| User-facing release notes | [release/CHANGELOG.md](release/CHANGELOG.md) |
| Deliberate Brio divergences (do-not-copy) | [brio/known-brio-bugs.md](brio/known-brio-bugs.md) |
| Parity tracking (non-normative) | `brio/parity-checklist.md` |
| Dated audit snapshots (non-normative) | `validation/*.md` |
| Backlog | `backlog/PBI-*.md` |

Rules: one normative home per contract — link, never restate. New documents
only for durable concepts with non-obvious invariants (see `AGENTS.md`).
Brio/Ktisis references only where they explain an intentional compatibility
decision. Delete superseded prose; do not archive it.
