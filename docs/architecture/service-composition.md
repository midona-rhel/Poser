# Service composition

## Purpose

The plugin composition root registers only dependencies reachable from the
focused product. Registration is not an archive of potential features.

## Modules

`AddDalamudDependencies` binds the host-provided Dalamud services.

`AddPoserCore` currently binds:

- configuration and the transitional event bus;
- GPose, actor, skeleton, actor-pose, bone-pose, and IK runtime services;
- clean scene/selection sessions, stable bindings, transform commands,
  gestures, pose use cases, and clean history;
- temporary adapters from clean ids/commands to legacy native entities.

`AddPoserFeatures` binds only retained vertical workflows:

- camera projection required by viewport picking;
- minimal animation runtime required by posing and acceptance;
- basic actor spawn/clone/destruction;
- gaze and expression controls;
- pose files;
- the focused live test service and command router.

`AddPoserPresentation` binds the main/settings windows, skeleton/gizmo canvases,
graphical bone pane, pose panes, `UiWindowSet`, and `UIManager`.

## Startup

Plugin startup resolves configuration, then eagerly activates
`CleanSceneLifecycle`, then constructs `IUIManager`. The explicit lifecycle
resolution is required because singleton registration is lazy: its constructor
subscribes actor, skeleton, and GPose events and performs the initial
`StableBindingRegistry` → `SceneSession` refresh. Constructing presentation
without it leaves the scene snapshot permanently empty.

Startup then registers the command and initializes the UI font/theme bridge.
Autosave, public IPC, web API, projects, libraries, and other deferred features
are not eagerly started.

## Disposal

The DI container owns singleton disposal. Window construction belongs to DI;
`UiWindowSet` only registers draw order and detaches the skeleton-toggle event
before removing windows from Dalamud's `WindowSystem`.
