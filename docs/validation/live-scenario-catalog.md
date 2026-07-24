# Focused live scenario catalog

`LiveScenarioCatalog` contains exactly seven rewrite contracts:

```text
selection.actor-bone-clear
transform.actor-components
transform.actor-undo-redo
posing.bone-components
posing.animation-interference
posing.reset-region
posing.copy-paste-pose
```

`Basic` and `Executable` intentionally contain the same ids. The difference is
repetition: bare `/poser test` runs once; `/poser test full` runs eight times.

There is no planned product backlog in this class. Camera, lighting, world,
environment, persistence, animation-browser, appearance, IPC, and UI work is
tracked in feature documentation and parity checklists. Adding a product feature
does not expand the rewrite gate.

At the end of a run, catalog/implementation mismatch is a failed
`coverage.catalog` result.
