# UiWindowSet

## Purpose

`UiWindowSet` is the typed draw-order owner for the reduced presentation. It is
not a generic feature-window registry.

## Owned surfaces

| Surface | Role |
|---|---|
| `MainWindow` | actor/bone tree, pose workspace, inspector |
| `SettingsWindow` | retained configuration |
| `SkeletonOverlayWindow` | viewport bone picking |
| `GizmoOverlayWindow` | viewport transform manipulation |

`GraphicalBonePane` is constructed by DI and injected into `MainWindow`; it is
not added to the `WindowSystem`.

## Lifecycle

`SetPrimaryOpen` opens or closes the main surface and both interaction canvases
with GPose. Settings remains independently user-controlled. The constructor
wires the main titlebar skeleton toggle to the skeleton canvas. `Dispose`
unwires that event and clears the window system.

Auxiliary close policies, pop-outs, and cross-window inline sharing are not part
of this class.
