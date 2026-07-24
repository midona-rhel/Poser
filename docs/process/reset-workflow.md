# Focused product reset workflow

## Goal

Delete product breadth before creating new abstractions. The reset retains one
complete posing workflow and makes each later feature earn a vertical slice.

## Ordered slices

### 1. Establish scope

Update product, UI, core, and reference documentation. Freeze deterministic
visual baselines for the main shell, actor selection, bone selection, Body,
Face, Matrix, 3D, collapsed shell, and Settings.

### 2. Reduce presentation

Remove independent spawn and status/VFX windows, then remove deferred routes:
Animation, Appearance, Camera, Light, Environment, Library, and Reference.
Convert graphical bone selection from a window into a pane. Reduce
`UiWindowSet` and `UIManager` to the retained surfaces.

### 3. Reduce the UI implementation

Completed: the standalone UI host, unused controls, and retained-node demo
engine are gone. The surviving renderer and widgets are one physical
`Poser.UI` project, exercised only through the plugin.

### 4. Remove unreachable feature clusters

Completed for the deferred product clusters: registration and eager startup
were removed before their services, interfaces, entities, embedded data, and
documentation. The remaining `PosingCore` code is in the active posing
dependency closure.

### 5. Finish the clean posing runtime

Clean transform history is now the sole journal and `TransformRuntimePort`
directly owns the application-to-runtime transform boundary while preserving
the proven hook order. The remaining work is replacing compatibility
selection/entity projections with application workspace state.

### 6. Collapse projects

After the legacy backend is gone, merge Domain and Application into
`Poser.Core`, rename Game to `Poser.Runtime`, and delete `PosingCore`. The UI
project collapse is already complete and is not part of this final backend
rename.

## Gate for every slice

1. production build;
2. user review in game when the slice changes UI;
3. `/poser test` for normal confidence;
4. `/poser test full` before deleting a proven native path.

The live test verdict comes from `run.json`; command timing or chat text is not
a success signal.
