# SceneSession

## Purpose

`SceneSession` is the application authority for the current logical scene. It
contains actor/skeleton/bone descriptors received from the runtime adapter and
exposes generation-safe lookup without retaining native entities.

## Refresh

The game adapter supplies a complete `SceneSnapshot`. `Refresh` atomically
replaces the registry and then reconciles application selection:

- identical ids remain selected;
- a newer generation of the same logical actor may replace an old selection;
- bones rebind only when actor lineage, slot, partial, index, and canonical name
  match;
- missing targets are removed.

## Descriptors

Descriptors contain names, hierarchy, and capabilities needed by application
logic. They contain no pointers, Dalamud objects, UI state, or mutable native
handles.

## Ownership

The scene session owns `SelectionSession`. Pose state remains owned by the
runtime/domain pose store and is accessed through explicit application ports
until the full pose evaluator migration is complete.
