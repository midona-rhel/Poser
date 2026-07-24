# UI runtime

## Purpose

The UI runtime owns presentation lifetime and draw order for the reduced Poser
product. It is intentionally not a general window manager.

## Surfaces

`UiWindowSet` owns exactly:

- `MainWindow`;
- `SettingsWindow`;
- `SkeletonOverlayWindow`;
- `GizmoOverlayWindow`.

`GraphicalBonePane` is normal main-window content. It owns Body and Face texture
resources and drawing state, but has no `Window` lifecycle.

The overlay windows open and close with the main GPose workspace. Settings is
user-controlled. Leaving GPose closes the main surface and overlays; it does not
need a generic “close all detached windows” policy.

## UIManager

`UIManager` connects Dalamud draw/open callbacks, GPose visibility, settings,
and retained keybind edges. It does not construct windows, coordinate feature
pop-outs, repair reference-image selection, or subscribe to auxiliary feature
events.

## Dependency rule

The main window receives focused panes and application/runtime ports. It does
not receive other windows. A piece of main content that needs independent
lifetime or state becomes a pane/component first; adding a new product window
requires an explicit product-scope update.

## Physical project

The former renderer and widget projects are one physical `Poser.UI` assembly.
It contains only the layout, glass, typography, icon, input, SVG, and widget
primitives used by the in-game main window and settings. Poser has no
standalone UI project, alternate host, or Dalamud shim.
