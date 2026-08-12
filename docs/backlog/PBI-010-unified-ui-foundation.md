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
- The retired Norvrandt/stylesheet layout engine stays deleted. Product code
  uses Crystarium directly.
- Crystarium is the single product-facing component and composition API.
- `AppShellView` owns only shell composition and tree/canvas-specific drawing;
  it consumes shared tokens and controls instead of defining replacements.
- Panes own state and callbacks only. They may not create another button,
  field, row, section, tooltip, popup, glass, scrollbar, or text-baseline
  recipe.

Do not add another UI project, framework, stylesheet layer, or generic widget
library. Delete unused general-purpose rendering code rather than preserving it
"for later".

## Authoring contract

Centralized numbers are insufficient if every pane still assembles property
records and handles immediate-mode plumbing. The product-facing API must be
small enough that normal UI reads as content:

```csharp
Crystarium.Page("appearance", origin, size, page =>
{
    page.Actions(actions =>
    {
        actions.Button("Open in Glamourer", OpenGlamourer,
            disabled: !available, help: unavailableReason);
        actions.Button("Reset appearance", ResetAppearance);
    });

    page.Section("PRESENTATION", form =>
    {
        form.Slider("Opacity", opacity, 0f, 1f,
            value => SetOpacity(value), format: "0.00");
        form.Switch("Override", overrideOn,
            value => SetOverride(value));
    });
});
```

Exact names may follow C# conventions, but preserve the shape:

- composition scopes and standalone controls expose the same short semantic
  methods;
- current value and change callback are supplied together;
- the composition owns IDs, density, classes, sizing, placement, help target,
  change detection, and drawing;
- routine behavior uses named arguments; deliberate presentation overrides use
  one small reusable typed style value, not tag-specific property bags;
- size is a passable semantic value (Content, Fill, Workspace, Comfortable, or
  an exceptional fixed size), shared by primitives and compositions;
- explicit IDs and low-level styling are internal escape hatches, not normal
  pane code;
- no `UiAction`, `ColorWellValue`, tag-specific props, style classes,
  `ref`-plus-`if changed`, or manual measurement boilerplate appears in a
  migrated product pane.

This remains immediate-mode UI. Do not build a retained virtual tree, allocate
a component object graph every frame, add reflection/markup/source generation,
or introduce `New(...).Build().Draw()` ceremony. Existing simple calls such as
`Crystarium.Button("Hello", onClick)` are the preferred primitive shape; extend
that simplicity rather than wrapping it.

`Theme` must be a complete replaceable value, including metrics. Static
`Theme.Metrics` constants are not themeable and therefore do not satisfy this
PBI. Move the canonical metric groups into the active theme value so one call
loads colors, typography, metrics, radii, shadow, motion, and optical
corrections together. Support:

```csharp
Crystarium.UseTheme(Theme.PictoDark with
{
    Controls = Theme.PictoDark.Controls with { WorkspaceHeight = 28f },
});
```

All primitives and compositions resolve the active theme. Scoped overrides may
exist for exceptional canvases, but product panes cannot mutate global style
state or override individual pixel values.

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
   Section/Form/FormRow/ScrollRegion/FloatingSurface plus the concise authoring
   API above. Correct the current menu blur/shadow/hit-geometry defects while
   extracting the surface.
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

## Live correction tranche

Complete this tranche before accepting the Pose, Animation, or shell slices.
These are systematic composition fixes, not pane-local offsets.

### Stable shell and glass

- The sidebar, main content box, optional inspector rail, inset, and scrollbar
  gutter keep identical rectangles across Pose, Animation, and Appearance.
  Changing tabs may change contents and rail visibility, never the outer
  window or the surviving content rectangle.
- Apply one background blur behind the shell chassis, then draw the titlebar,
  sidebar, content, and rail with theme-owned translucent fills and directional
  borders. Do not blur each panel independently or duplicate shadows.
- Keep the accepted overlay rule without hiding the whole world gizmo:
  interactive UI rectangles clip gizmo paint and reject gizmo hit-testing only
  where they overlap. Moving the pointer over the Poser window must not erase
  portions of the gizmo that remain visible in the game viewport.
- Keep the 12 px scrollbar gutter and hit area. Make only the visible thumb
  20% narrower and center it in that gutter through the one shared scrollbar
  path; do not reclaim layout width.

### Shared-control defects

- Pose footer labels, checkboxes, and Clear use one FormRow centreline; remove
  its separate text/checkbox/button offsets.
- Dropdown rows own disjoint 26 px hit rectangles. Hover is neutral and unique;
  the selected row uses a distinct selected treatment, so selected plus hovered
  never looks like two hovered rows. Separators remain visible and span the
  usable list width. A non-overflowing list does not reserve a phantom visual
  strip, although its scrollbar gutter contract remains stable.
- Numeric axis wells are a Crystarium primitive, including Hinge axis. They
  use the mono value font, theme focus/selection colors, right-aligned values,
  and widths derived from the FormRow region. Raw `InputFloat` styling and
  overflowing fixed indents are deleted. Transform wells display one more
  digit than today: Translation and Scale use `0.000`; Rotation uses `0.00`.
  Hinge axis uses `0.000` and supersedes the earlier single-row decision: its
  label occupies the normal form row and the X/Y/Z wells occupy one full-width
  row immediately below, matching the transform triplet instead of squeezing
  three values into the post-label column.
- The Tint row remains one row, not three. Its Character/Main/Off groups use
  three equal tracks across the control region, one shared inline gap between
  each label and 26 px well, and the FormRow centreline. Missing models reserve
  the same track and well geometry so availability never reflows the row.
- Body/Face maps resolve all overlapping dot candidates before painting and
  render exactly one hovered instance. Clicking and HoverHelp use that same
  winner.
- Map-side mirroring is stored in `UIConfiguration` and survives relaunch.
- The transform toolbar always reserves the Self/Parent pivot selector. It is
  disabled when the active tool/selection cannot use it, and Parent alone is
  disabled when no parent exists. Remove the ambiguous chain-link toolbar
  button; automatic linked-bone groups remain the one behavior until a future
  explicit product control is designed.
- Move Physics freeze to one actor-scoped toolbar icon backed by
  `AnimationSession`; remove the duplicate Pose-header Physics switch.

### Sidebar semantics

- Remove the right-hand `player`/`npc`/`minion`, bone counts, and canonical or
  Japanese bone-name badges. Rows show the user-facing name only; technical
  identity may appear in HoverHelp or diagnostics.
- Give rows a fixed right action strip that is excluded from the row selection
  hit rectangle. Actor rows expose: set game target, show/hide actor, and
  pause/resume animation. These call the existing actor and animation services;
  they do not own duplicate booleans. Hidden/paused state has a clear dimmed or
  struck visual treatment and remains reversible.
- Remove the titlebar “select current in-game target” shortcut. Selecting an
  actor is done by its row; setting the game target is the actor-row/context
  action.
- Category and bone rows may toggle skeleton-overlay presentation only.
  Category changes cascade to descendants; rows remain in the tree so the
  action can be reversed. This mask must not hide the actor mesh, mutate the
  native skeleton, change selection, or alter pose import/export.
- Actor context menus retain target, visibility, pause/resume, rename, clone,
  companion detach, and spawned-actor despawn. Bone menus retain hierarchy
  selection, mirrored selection, flip, and reset. Overlay visibility belongs
  on category/bone rows and their menus. Do not add unavailable attachment or
  weapon actions as decorative placeholders.

### Popup ownership

- Floating menus close on the first outside press, Escape, owner disappearance,
  and a second press on the same plus/owner button. Opening another floating
  surface replaces the old one. The dismissing click never reaches the game or
  an underlying control.
- Menu and dropdown row rectangles must be non-overlapping at every supported
  scale. At most one option receives hover, press, HoverHelp, or activation in
  a frame, and visual animation uses the same rectangle as hit-testing.
- Floating-surface occlusion is centralized. Every menu, dropdown, picker,
  color well, modal, file window, and hover card registers its surface and
  input layer with the shared interaction path. `Interactive.Reserve` rejects
  lower-layer hover/clicks, and HoverHelp revalidates its final candidate after
  all windows draw. No tooltip or click may bleed through a color picker or
  other floating surface, including on the frame it opens.

### Retained pages and matrix

- Appearance starts with a normal `GENERAL` section containing opacity and
  model tint, followed by Wet Surface and the existing external/file sections.
- Animation uses the same Page/Section/Form grammar as Appearance. General
  playback, Stance, Layers, Face & Lips, Advanced Slots, and Advanced Controls
  all use the same persisted disclosure component; closed content contributes
  zero height. The actor heading uses the shared lineage display API, so
  nickname and anonymous mode are never bypassed.
- Selection propagation is tab-independent. `PoseInspectorPane.SetSelection`
  currently runs only inside the Pose content branch, leaving the always
  visible inspector rail on Animation/Appearance bound to a stale bone. Update
  the inspector selection once per main-window frame before tab dispatch, then
  render any tab. IK eligibility, transform values, and every rail section must
  update immediately without visiting Pose first.
- Treat Matrix as a bounded canvas with equal theme page padding on all four
  sides. Middle-drag pans; wheel zooms about the pointer; a small Reset View
  action restores fit. Selection semantics and matrix filtering are unchanged.
  Truncation uses the single ellipsis glyph `…`, not three periods rendered as
  dashes by the mono font.

Weapon display names and drag-to-attach are deliberately not part of this UI
tranche. Ktisis resolves a weapon model tuple through the item sheet, and its
link/drag affordance is backed by attachment ownership and native restoration.
Those require a separate stable-id runtime PBI; this PBI must leave the current
Main Hand/Off Hand slot labels truthful rather than fake either capability.

## Static completion gates

- Zero ordinary `ImGui.Button`, `Selectable`, `Checkbox`, `Slider*`,
  `DragFloat*`, `InputText`, `Combo`, `BeginCombo`, `MenuItem`, raw tooltip,
  or raw popup calls outside primitive implementations.
- Zero references to `InspectorLayout`, `UIConstants`, or
  `Poser.UI.Controls.Layout`.
- Zero pane-local definitions of row height, control height, label/value
  columns, page padding, section gap, scrollbar size, or glass chrome.
- Zero tag-specific property-bag/style/sizing construction in migrated product
  panes; normal authoring uses semantic scope methods, named behavior, and the
  shared reusable control-style value.
- The active `Theme` value contains metrics; no static `Theme.Metrics` consumer
  remains.
- Zero Norvrandt or stylesheet layout/rendering code remains.
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
dotnet build Poser.slnx -c Release --no-restore --nologo
git diff --check <slice-base>..HEAD
```

Report the slice commit range, deleted competing paths, canonical metrics used,
remaining migration surfaces, and the exact in-game visual checks. Do not claim
visual success.
