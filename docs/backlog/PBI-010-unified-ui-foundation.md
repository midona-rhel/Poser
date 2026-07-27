# PBI-010 — Unified UI foundation and retained-workspace migration

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Extra large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game after each slice |
| Base ref | `pbi-010-base` |
| Feature branch | `feature/pbi-010-unified-ui-foundation` |
| Accepted head | Not accepted |

The reset starts from PBI-009 head
`f9e280fdeae78c2a152478bdae98fbc9b2422e44`. PBI-009 behavior remains the
baseline. This PBI pauses new product features and replaces the retained UI's
competing layout and component grammars.

## Problem

The retained workspace currently mixes:

- theme/stylesheet metrics (`Theme`, `DefaultStylesheet`);
- low-level Norvrandt layout and rendering;
- Crystarium primitives;
- `InspectorLayout`;
- hand-positioned `AppShellView`;
- pane-local row helpers, widths, padding, and optical offsets.

Consequently a row or control may be 24, 26, 28, 30, or 32 pixels depending on
which path drew it. Menus, dropdowns, popovers, modals, and file windows compose
glass independently. Pages repeatedly calculate label columns, control
positions, readout columns, action gaps, section spacing, and scroll extents.
Fixing one screen therefore creates another local convention.

## Outcome

One UI foundation owns every ordinary retained control, metric, layout row,
floating surface, and optical baseline. Product panes describe content and
callbacks; they do not position ordinary widgets.

The retained UI is the main shell, sidebar/tree, Pose surfaces and inspector,
Animation, Appearance, settings, pickers, menus, and file dialog. Canvas
geometry for Body/Face/Matrix/3D and world/inspector gizmos remains specialized,
but any controls around those canvases use the shared foundation.

This is consolidation, not a new visual redesign. Picto remains the chrome and
interaction reference. Existing accepted Poser composition remains unless this
PBI explicitly normalizes it.

## One ownership model

- `Theme` owns all colors, typography, radii, shadows, spacing, control sizes,
  optical corrections, and shell dimensions.
- Norvrandt is an internal renderer/layout engine. Product code under `Poser/`
  must not consume it directly.
- Crystarium is the single product-facing component and composition API.
- `AppShellView` owns only shell composition and tree/canvas-specific drawing;
  it consumes shared tokens and controls instead of defining replacements.
- Panes own state and callbacks only. They may not create another button,
  field, row, section, tooltip, popup, glass, scrollbar, or text-baseline
  recipe.

Do not add another UI project, framework, stylesheet layer, or generic widget
library. Delete unused general-purpose rendering code rather than preserving it
"for later".

## Canonical metrics

Put these in one named metric source. Consumers request a semantic variant;
they never repeat the number.

| Metric | Value | Use |
|---|---:|---|
| Space 1/2/3/4/6/8 | 2/4/6/8/12/16 px | all gaps and padding |
| Page inset | 12 px | main content and rail |
| Section gap | 12 px | between complete sections |
| Inline action gap | 8 px | buttons/fields on one row |
| Form row | 30 px | label, control, optional value |
| Main-workspace control | 26 px | buttons, dropdowns, inputs in forms |
| Comfortable control | 32 px | settings and modal forms only |
| Navigation/segmented | 30 px | tab and mode navigation |
| Shell icon action | 28 px | titlebar/toolbar icon buttons |
| Sidebar/menu/list row | 26 px | tree, pickers, dropdowns, menus |
| Label/value columns | 94/44 px | retained inspector/form grammar |
| Titlebar/toolbar/status | 48/44/26 px | shell only |
| Sidebar/rail widths | resizable 220–400 / fixed 280 px | shell only |
| Scrollbar/gutter | 12 px | every scrolling region |

Only two ordinary control densities exist: 26-pixel main-workspace and
32-pixel comfortable modal/settings. A row cannot mix them. Navigation and
shell icon actions are separate semantic components, not button-size variants.

Typography is likewise tokened: caption 11, label 12, body/control 13, surface
title 14. Existing component-level optical corrections remain centralized and
are applied after scaling with framebuffer snapping. No pane-local ±1 pixel
adjustment is permitted.

## Required shared compositions

Implement these as the narrow product-facing API over retained primitives:

1. **Page** — one inset, optional action bar, content extent, and scroll-gutter
   contract.
2. **ActionBar** — left and right groups, one height, eight-pixel gaps, exact
   alignment with page content.
3. **Section** — one caption/disclosure style, exact open/closed height,
   section gap, separator, and persisted disclosure state supplied by caller.
4. **Form** and **FormRow** — 94-pixel label column, control region, optional
   44-pixel value region, vertical centring, row-level HoverHelp, and explicit
   span support.
5. Typed rows for slider, switch, dropdown, text input, buttons, color wells,
   axis wells, read-only value, status/progress, and custom canvas content.
6. **ScrollRegion** — stable 12-pixel gutter, one scrollbar style, no overlay
   on content, and no scrollbar when content fits.
7. **FloatingSurface** — one glass fill/blur/border/shadow and placement host
   shared by menus, dropdown lists, popovers, modals, color wells, and file
   dialogs. Surface behavior differs; chrome does not.

The typed rows compose existing primitive behavior. They do not duplicate
hit-testing or drawing.

## Migration rules

- No raw ordinary ImGui widget calls outside `Poser.UI` primitives.
- No manual `SetCursorScreenPos` for ordinary form, toolbar, or action-row
  layout in product panes.
- Numeric canvas coordinates remain allowed only in the graphical maps,
  matrix/3D canvas, rotation/world gizmos, and skeleton overlay.
- Component widths use available layout slots or named semantic widths.
  Pane-local `Sizing.Fixed(...)` is allowed only for documented canvas
  geometry, never to make ordinary controls line up.
- Controls in the same row have the same semantic height.
- Hidden/collapsed content contributes zero height; absent values use the
  shared unavailable state and never invent another row style.
- Floating surfaces never draw their own glass or shadow recipe.
- HoverHelp is the only explanatory tooltip.
- Tabler/retained custom icons are the only icon source.

## Retained-surface migration order

Each slice is built and inspected in game before the next starts. Do not land
all migrations and ask for one final visual pass.

1. **Foundation** — canonical tokens, primitive variants, Page/ActionBar/
   Section/Form/FormRow/ScrollRegion/FloatingSurface. Correct the current menu
   blur/shadow/hit-geometry defects while extracting the surface.
2. **Appearance** — migrate the entire page, external selectors, MCDF row, and
   file dialog. This is the proving slice; the user accepts its padding,
   control heights, section rhythm, floating chrome, and scroll behavior.
3. **Animation** — replace its local rows, sections, buttons, sliders, slot
   disclosures, picker geometry, and status layout without changing playback.
4. **Pose inspector and rail** — translation/rotation/scale, expression, gaze,
   IK, pose/files, parenting footer, and inspector header.
5. **Shell/sidebar/pickers/settings** — tree rows, toolbar/titlebar/status,
   dropdowns, context/add menus, external/animation pickers, and settings.
6. **Deletion** — remove `InspectorLayout`, `UIConstants`, the unused
   `Controls/Layout` facade, duplicate glass/modal/menu recipes, dead style
   classes, and any now-unreferenced rendering generality.

Do not migrate a surface by wrapping its old manual helper inside a new name.
The old positioning code is deleted in the same commit that migrates it.

## Static completion gates

- Zero ordinary `ImGui.Button`, `Selectable`, `Checkbox`, `Slider*`,
  `DragFloat*`, `InputText`, `Combo`, `BeginCombo`, `MenuItem`, raw tooltip,
  or raw popup calls outside primitive implementations.
- Zero references to `InspectorLayout`, `UIConstants`, or
  `Poser.UI.Controls.Layout`.
- Zero pane-local definitions of row height, control height, label/value
  columns, page padding, section gap, scrollbar size, or glass chrome.
- `Poser/` has no direct Norvrandt layout/rendering calls except the approved
  shell/canvas bridge identified in the handoff.
- One floating-surface chrome path and one scrollbar path.
- Every deleted path is reference-proven dead.

Use a small checked allowlist for canvas math; do not weaken these gates with a
broad directory exemption.

## Visual acceptance

At 100%, 125%, and 150% UI scale:

- the same component has the same dimensions and padding on every page;
- buttons, dropdowns, inputs, and wells in one row share a centreline and
  semantic height;
- page and section edges align across Pose, Animation, Appearance, and
  settings;
- no list item, toolbar, footer, modal, or form invents another text baseline;
- scrollbars appear only when needed, reserve the same gutter, and never cover
  content;
- menus, dropdowns, popovers, color wells, and file dialogs visibly share one
  glass surface;
- collapsed sections contribute only their header;
- changing tabs does not leave stale width, padding, or scrollbar state;
- all pre-existing interactions and PBI-009 ownership behavior remain intact.

The user accepts each migration slice in game. Compilation is integration
evidence only and cannot close a slice.

## Excluded

- New posing, animation, appearance, spawn, prop, camera, light, reference,
  library, or scene functionality.
- Changes to transform math, native runtime behavior, selection identity,
  pose/animation ownership, or MCDF transactions.
- Redesigning canvas imagery or gizmo geometry.
- DevHost, npm, screenshot automation, a UI test framework, or component
  documentation.

## Build and handoff

After each accepted slice, run only:

```text
dotnet build Poser.slnx -c Debug --no-restore
git diff --check <slice-base>..HEAD
```

Report the slice commit range, deleted competing paths, canonical metrics used,
remaining migration surfaces, and the exact in-game visual checks. Do not claim
visual success.
