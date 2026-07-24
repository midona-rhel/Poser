# Project dependency rules

## Current graph

```text
Poser.Domain      → nothing
Poser.Application → Poser.Domain
PosingCore        → Dalamud and FFXIVClientStructs
Poser.Game        → Domain, Application, PosingCore
Poser.UI          → retained rendering dependencies
Poser             → Domain, Application, Game, PosingCore, UI
```

This graph exists to migrate safely; it is not the desired package structure.

## Target graph

```text
Poser.Core    → nothing
Poser.Runtime → Poser.Core, Dalamud, FFXIVClientStructs
Poser.UI      → retained rendering dependencies only
Poser         → Core, Runtime, UI
```

`Poser.Domain` and `Poser.Application` become `Poser.Core` only after legacy
runtime mutation is gone. `Poser.Game` becomes `Poser.Runtime`, and
`PosingCore` is deleted. The UI project merge is complete.

## Rules

1. Core never references Dalamud, ImGui, native structures, storage, or IPC.
2. Runtime never references product UI.
3. UI dispatches application commands and projects application state; it does
   not own persistent pose state, native writes, selection, or history.
4. Raw pointers and object addresses never cross the runtime boundary.
5. Brio and Ktisis are read-only local references, never solution dependencies.
6. A new physical project requires an independent consumer or deployment
   boundary. Logical folders are preferred otherwise.
7. Transitional dependencies may shrink but may not point backward.
