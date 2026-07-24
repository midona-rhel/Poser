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

`SetPrimaryOpen` opens or closes the main surface and the gizmo canvas with
GPose. The skeleton overlay starts **Off** each GPose/UI session: only the
toolbar Armature action opens it, its active state reflects the actual window
state, a user toggle persists while the session remains active, and session
end closes it so the next session starts Off again. While the overlay is
open, holding **Alt** temporarily hides the skeleton dots for an
unobstructed view; drawing resumes on release. Disabling skeleton dots
never disables transform manipulation — the gizmo canvas is independent.
Settings remains independently user-controlled. The constructor wires the main
titlebar skeleton toggle to the skeleton canvas. `Dispose` unwires that event
and clears the window system.

Auxiliary close policies, pop-outs, and cross-window inline sharing are not part
of this class.
