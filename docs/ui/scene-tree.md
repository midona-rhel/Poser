# Scene tree

The scroll child always reserves a 12 px right-hand scrollbar gutter without
forcing a scrollbar thumb when the tree fits. Filtered and collapsed trees
therefore use the same content width as overflowing trees; showing a scrollbar
never shifts row labels, right-aligned counts, or section actions. The tree's
12 px left padding equals its right composite gutter (0 px content gap + 12 px
scrollbar), and the scrollbar viewport reaches the panel's outer-right edge.

Tree guide columns are anchored to the center of the actor icon: the first
vertical trunk therefore continues directly beneath that icon, and deeper
columns repeat on a 20 px grid. Continuation lines, expanding branches, and the
last child's hard L all use the same guide-coordinate function. The terminal
connector is a hard 90-degree corner made from two edge-joined filled legs. The
vertical leg owns the square corner and the horizontal begins at its outer edge,
so the translucent pieces never overlap. Its horizontal arm ends at the same
8.5 px coordinate as every non-terminal branch, so terminal rows do not have a
wider connector. Carried vertical trunks are filled segments whose edges meet
at row boundaries; there are no overlapping semi-transparent line caps to
darken the joints.

Nested selection pills begin 10 px to the right of their active guide: 1.5 px
after the shared branch endpoint and 4 px before the label. Connector ink never
runs beneath the active background. Section plus actions use the standard 14 px
Tabler Plus centered in an inset 18 px hitbox, so the scrollbar gutter cannot
clip the icon.

## Purpose

The scene tree is the left sidebar's primary selection surface. It presents
actors, actor bone categories, and bones in one stable hierarchy. Selection
drives the main Pose workspace and adjacent inspector rail; it does not open a
separate actor-properties workflow.

## Hierarchy

- Actor root rows represent the complete draw object. Selecting one exposes whole-actor transform, animation, and delegated appearance controls.
- Bone categories are navigation-only grouping rows. They never become the transform target.
- Bone leaf rows represent parent-local skeleton transforms.
- Hidden actors remain selectable and carry a **hidden** badge; **Show/Hide** is available from the actor context menu through `IActorSpawnService`.

The titlebar User Circle action selects the current GPose target in Poser. The
actor context action **Set game target** performs the opposite direction. This
keeps the two operations explicit instead of overloading one ambiguous
“Target” command.

## Disclosure state

- Every actor root first appears **collapsed** in a new `MainWindow` session
  (PBI-002 supersedes the earlier expanded default). Bone categories also
  first appear collapsed. Both are seeded into the collapse set on first
  sight, so newly added or refreshed actors arrive collapsed without touching
  any existing row's state.
- Actor and category rows share one disclosure affordance: the registered
  Tabler Chevron Right/Down icon with an 18 logical-pixel hit zone, visible
  in both collapsed and expanded states. Clicking the chevron toggles
  expansion without selecting; clicking the row selects without changing
  expansion. An actor whose skeleton is temporarily unresolved shows the
  chevron faded and inert — it is never erased.
- A category containing a bone whose display name IS the category name
  (Root → `n_root` "Root") renders that bone AS the category row instead of
  a redundant Root > Root pair: the row body selects the bone, its chevron
  discloses the category's remaining bones, and its badge counts them.
- A user collapse or expand action persists for the lifetime of the window.
- When a bone selection originates elsewhere (body map, matrix, 3D, overlay,
  gizmo), the selected bone's actor and category are revealed **once, at the
  moment the primary selection changes** — never re-forced on later frames.
  The user may re-collapse the revealed rows immediately and that choice
  sticks while the selection is unchanged.
- Selecting an actor row does not expand the actor.
- Adding or refreshing another actor does not expand any existing actor.

## Filtering

The sidebar filter searches the complete scene, case-insensitively:

- Actors match display names and raw game names.
- Bones match localized display names, raw bone names, and category display names.

Filtering preserves hierarchy: a matching bone keeps its actor and category ancestors visible. A matching category reveals all bones in that category; matching an actor does not flood the result with every bone. Matching ancestors are forced open while the filter is active without mutating the stored disclosure state, so clearing the filter restores the user's prior collapse/expand choices exactly.

The filter uses the shared clearable search-field contract documented in
`docs/ui/search-fields.md`. Its trailing action empties the live filter
immediately and is absent when there is nothing to clear.

## Selection contract

| Input | Result |
|---|---|
| Click | Replace the selection and make the clicked entity primary. |
| Ctrl + click | Toggle the entity in the current selection. |
| Shift + click | Add the visible range from the selection anchor to the clicked entity. |
| Click category caret/row | Expand or collapse only; categories are not entities. |
| Right-click actor | Open target, Hide/Show, rename, clone, companion, and spawned-actor despawn actions without changing transform semantics. |

Every selected row receives the active treatment, while the first selected id remains the inspector's primary target. Multi-bone rail operations read the complete selection from `SelectionSession.Selected`. Incompatible Ctrl/Shift targets replace the selection: actors never coexist with bones, and bones from different actors never form one transform group. The session enforces this; the tree does not pre-filter.

## Ownership and data flow

- `MainWindow.BuildSidebar` converts `SceneSession.Snapshot` descriptors and filter state into `ShellSidebarSection` and `ShellSidebarRow` view models. Actor rows carry `SelectionId.ForActor`, bone rows carry `SelectionId.ForBone`; category rows carry a UI-only string key and never a selection id.
- Bone categories are derived in the UI from each descriptor's canonical bone name via the static bone-metadata table; they are presentation grouping, not snapshot or selection identity.
- `MainWindow.OnRowClicked` interprets keyboard modifiers and delegates selection mutation to `SelectionSession` (`Select`, `Toggle`, `SelectRange` with the visible compatible `SelectionId` order).
- `AppShellView.DrawSidebar` owns only drawing, scrolling, search-field input, and pointer hit testing.
- Collapse state belongs to `MainWindow`, not descriptors, because category rows are view-only groupings. Actor and category keys are actor-lineage-based and seeded collapsed on first sight; because the keys survive actor refreshes and redraws, a refresh cannot reset an existing actor's disclosure.
- External selection reveal resolves the newly selected bone id against the snapshot to find its actor row and derived category, then expands both exactly once per selection change.

## Reference decisions

- **Ktisis:** `Ktisis/Interface/Components/Workspace/SceneTree.cs` establishes inline scene selection, expansion, modifier-aware multi-select, and adjacent editor routing.
- **Brio:** `Brio/UI/Entitites/EntityHierarchyView.cs` reinforces Ctrl-toggle selection and entity-local context actions; Brio's bone-search surfaces establish that large skeletons require direct filtering.
- **Picto:** supplies the compact row, guide-line, and restrained active-state visual grammar used by `AppShellView`.

## Known risks and verification

- Filtering is rebuilt from the scene snapshot every frame; verify acceptable cost with several actors and modded skeletons.
- Range selection follows currently visible compatible entity rows, so collapsed,
  filtered-out, and differently typed entities are intentionally excluded.
- In-game verification must cover actor-root selection, category collapse persistence, external bone selection reveal, actor/bone/raw-name searches, Ctrl toggle, Shift range, and right-click actions.
