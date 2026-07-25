# PBI-003 — Documentation reset

## Control

| Field | Value |
|---|---|
| Status | Complete |
| Size | Medium |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User |
| Base ref | `pbi-003-base` (immutable annotated Git tag) |
| Feature branch | `feature/pbi-003-documentation-reset` |
| Accepted head | `0fd7b460f55acc86ba13efbac7cf2ca56db43dae` |
| Closed | 2026-07-25 |

## Outcome

A maintainer can understand Poser's product boundary, posing runtime, selection
and transform flow, retained UI, and extension rules without reading a
class-by-class documentation mirror.

Documentation states durable contracts once. Source code and XML comments
describe implementation details. Git history and completed PBI handoffs retain
historical reasoning.

## Why

The current `docs/` tree contains 91 Markdown files and roughly 4,270 lines.
Many files repeat:

- method and constructor inventories already visible in source;
- the same selection, gesture, gaze, and UI contracts;
- implementation sequences and historical migrations;
- verification instructions copied between PBIs and concept documents;
- Brio/Ktisis comparisons that no longer explain a live design decision.

This volume makes contradictions easier to create and important invariants
harder to find.

## Documentation rule

Create a document only when a concept has an independently useful product or
architectural contract.

A concept document should normally contain:

1. purpose and ownership;
2. externally visible behavior;
3. one or two invariants or boundaries that are easy to break;
4. a reference link only when it explains a deliberate compatibility choice.

Do not permanently document:

- public member tables;
- constructor dependencies;
- source-file lists that search can answer;
- line-by-line implementation flow;
- routine UI geometry;
- test walkthroughs;
- completed review findings;
- historical failed approaches;
- commit plans or agent instructions.

Most concept documents should be 10–40 lines. Central architecture documents
may be longer when the content is genuinely non-duplicated. Line count is a
guardrail, not a reason to omit a critical invariant.

For example, the durable gaze contract is sufficient at approximately:

> Gaze has Off, Forward, Camera, and Actor modes and independently controls
> Eyes, Head, and Body. Disabling a part immediately restores its pre-Poser
> state. Actor mode targets any other valid scene actor by stable identity.
> Reset restores every controlled part and clears gaze state.

Native offsets, transition mechanics, and debugging history belong beside the
unsafe implementation when they remain necessary.

## Scope

### Included

- inventory every active Markdown document;
- classify it as Keep, Merge, Historical, or Delete;
- consolidate duplicated contracts into a small canonical set;
- delete stale service catalogs, migration status, superseded plans, and
  duplicated UI implementation narratives;
- shorten completed PBIs to outcome, accepted range, deviations, and deferred
  work;
- add one `docs/README.md` map identifying the canonical document for each
  retained concept;
- fix surviving links after moves/deletions;
- rewrite `AGENTS.md` so documentation is concept-driven rather than requiring
  a separate file for every class, interface, service, or entity;
- retain necessary unsafe/native explanations as tight source comments.

### Excluded

- production behavior changes;
- renaming projects or namespaces;
- UI redesign;
- posing-core refactoring;
- generated API documentation;
- a documentation generator, linter, site, or new test framework;
- rewriting accurate code merely to make documentation simpler.

## Target structure

Prefer a small set resembling:

```text
docs/
  README.md
  architecture/
    product-and-boundaries.md
    posing-runtime.md
    application-state.md
    ui-workspace.md
  features/
    selection-and-transforms.md
    pose-operations.md
    expression-gaze-and-ik.md
    files-and-transfer.md
  process/
    external-implementation-review-loop.md
  backlog/
    PBI-*.md
```

The implementer may choose different names when existing accurate documents
already provide a better canonical home. Do not preserve directories merely to
match this sketch.

## Requirements

- One normative home per contract; other documents link to it instead of
  restating it.
- Active architecture/feature/process documentation should fit in no more than
  approximately 30 files and 2,500 lines, excluding proposed backlog items.
- No active document may describe a deleted type, registration, workflow, or
  behavior.
- Completed PBI files should normally remain below 100 lines.
- New PBIs should normally remain below 250 lines.
- Review findings stay in the handoff/task and Git history unless they change a
  durable contract.
- Brio/Ktisis references remain only where Poser intentionally depends on a
  non-obvious behavioral convention.
- Code comments explain unsafe offsets, native ordering, or surprising math;
  they do not narrate obvious code.
- Deletion is preferred over moving stale prose into an archive. Git already
  preserves it.

## Implementation order

1. Record the inventory and proposed Keep/Merge/Delete disposition in the
   implementation handoff, not as another permanent document.
2. Establish the canonical document map.
3. Merge durable content into the canonical set.
4. Delete superseded documents and shorten completed PBIs.
5. Update `AGENTS.md` and the external implementation process.
6. Run link and stale-symbol searches, then review the final tree as a reader.

## Acceptance

- [ ] `docs/README.md` lets a new contributor find every durable contract.
- [ ] Selection, transforms, pose runtime, retained UI, expression, gaze, IK,
      and file transfer each have exactly one normative home.
- [ ] Gaze and similarly small features are concise rather than service essays.
- [ ] No active class-by-class service catalog remains.
- [ ] No active migration-status or superseded-design document remains.
- [ ] PBI-001 is reduced to a concise historical record; the completed
      pose-workspace stabilization backlog is removed.
- [ ] `AGENTS.md` requires documentation only for durable concepts and
      non-obvious invariants.
- [ ] Every surviving local Markdown link resolves.
- [ ] Searches for deleted production types produce no misleading active docs.
- [ ] The final active documentation stays near the size guardrails without
      hiding an essential invariant.
- [ ] No production source file changes except necessary comment correction.

## Handoff

Claude reports:

- base and head commits;
- documents kept, merged, and deleted;
- final active file/line counts;
- canonical homes for the major concepts;
- stale-symbol and link-check results;
- any document retained above the normal size guardrail and why.

No screenshots, npm, DevHost, IPC, browser automation, or in-game verification
are required. This PBI changes documentation policy and content only.
