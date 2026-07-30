# PBI-011 — Component-by-component UI conformance

| Control | Value |
|---|---|
| Status | Slices 1–3 accepted; ready for slice 4 |
| Size | Extra large, delivered as individually accepted component slices |
| Implementation owner | Claude |
| Review owner | Codex |
| Runtime and visual acceptance | User |
| Base ref | `pbi-011-base` (`9a9193736b797f828b0336d9571721bb8ba90c99`) |
| Feature branch | `feature/pbi-011-component-conformance` |
| Accepted head | `d66806b` (slice 3 — Text buttons) |

## Problem

Poser now has shared theme tokens and primitives, but it still has parallel visual
recipes, inconsistent flow behavior, and missing composite components. Fixes made
in one pane regularly regress another. Matching a screenshot is not sufficient:
intrinsic sizing, allocated fill, hover ownership, dismissal, clipping, focus,
disabled state, and layout stability must also match.

## Outcome

Every retained piece of ordinary application chrome has one canonical
Crystarium implementation, one Picto-derived behavioral contract, and one
automated comparison fixture. Product panes compose those controls; they do not
draw local variants. The entire application supports the portable Picto themes
Dark, Light, Light Gray, Gray, Blue, and Purple. Acrylic, Mica, Liquid Glass, and
other platform materials remain out of scope.

Specialized posing canvases remain specialized: body/face maps, matrix content,
3D content, and gizmos are not converted into generic widgets. Their surrounding
tabs, toolbars, footers, scrolling, and input ownership are in scope.

## Non-negotiable delivery loop

Work on exactly **one numbered component slice at a time**:

Before slice 1, Codex accepts the harness baseline: source hashes, stale-reference
detection, complete catalog routing, six themes, and three scales.

1. Identify the exact current Picto TSX/CSS source and record it in the fixture.
   Do not substitute a visually similar component or invented sample data.
2. Inspect the TSX component itself: public props, controlled state, internal
   transient state, composition, DOM hierarchy, event flow, and CSS selectors.
   Translate that design deliberately into immediate-mode UI; do not copy only
   the final pixels or mechanically imitate React.
3. Define the smallest reusable, product-agnostic Crystarium API that covers the
   catalog fixture and every retained production consumer.
4. Add every required state to `tools/ui-conformance`: Picto reference,
   Crystarium candidate, red diff, metrics, and the visual inspection page.
5. Normalize the canonical Crystarium component and migrate every retained Poser
   call site that represents that component.
6. Delete the superseded component, local drawing recipe, token, and compatibility
   overload. Do not leave two valid ways to render the same ordinary control.
7. Run the Debug production build and `git diff --check`. Then three capture
   runs, each bounded at five minutes, splitting the axes by what they can
   actually detect — geometry is theme-invariant (Picto themes change color
   tokens only), so scales run against one theme and themes run against one
   scale instead of a full cross-product:

   ```powershell
   # complete catalog regression, Dark/100%
   .\tools\ui-conformance\run.ps1 all -Clean -Scales 1 -Themes dark
   # geometry: current component across scales, one theme
   .\tools\ui-conformance\run.ps1 <component> -Scales 1,1.25,1.5 -Themes dark
   # theme colors: current component across themes, one scale
   .\tools\ui-conformance\run.ps1 <component> -Scales 1 `
       -Themes dark,light,lightgray,gray,blue,purple
   ```

   The later runs preserve the earlier results in the same inspection window.

   Every report records the Picto-source manifest and candidate rendering manifest hash;
   the aggregate window marks preserved results stale instead of presenting
   them as evidence for a different build.
8. Stop and hand the slice to Codex with the API design, state ownership, source
   paths, changed paths, deleted paths, reports, residual pixels, and explicit
   in-game locations.
9. Codex reviews both the reusable code design and automated comparison.
10. The user inspects the comparison window and the same component in game.
11. Record the accepted commit below. Only then begin the next numbered slice.

Claude must not batch later components, continue while acceptance is pending, or
claim visual/runtime success from compilation or image metrics.

For this PBI, the focused `tools/ui-conformance` capture and pixel-diff workflow
is required and supersedes the general process prohibition on standalone UI and
pixel-diff harnesses. It does not authorize npm, DevHost, IPC click simulation,
generic screenshot automation, or generic unit-test infrastructure.

## Shared behavior contract

Each applicable fixture and implementation must exercise:

- intrinsic content width, exact fixed width, and bounded allocated fill;
- visual bounds, hit bounds, clipping, and scrollbar-gutter reservation;
- idle, hover, pressed, selected, focused/editing, disabled, and unavailable;
- press/release ownership, keyboard operation, Escape, and outside-click dismissal;
- one hovered item, one click owner, and no click-through or HoverHelp-through;
- stable geometry while state, selection, content, or top-level tab changes;
- correct persistence for user preferences and session state;
- identical ordering, padding, optical baselines, separators, radii, glass,
  shadow, and motion wherever Picto defines them.

Exact equality is the target. A broad tolerance or percentage score cannot accept
a component. Any unavoidable glyph-rasterization residual must be isolated in
the report and explicitly accepted by the user; it cannot hide geometry, color,
state, or interaction differences.

## Ordered component slices

The Picto reference column names the reference family; the implementation must
record the exact file and selector used when its fixture is added.

| # | Component | Picto reference family | Required Poser coverage | Accepted commit |
|---:|---|---|---|---|
| 1 | Text | Picto typography | Labels, captions, mono values, disabled text, wrapping and truncation | `02d25f7` |
| 2 | Icons | Picto Tabler icon use | Optical alignment, size, stroke, tint and disabled state | `b44a2f3` |
| 3 | Text buttons | `actionButton` | Primary/secondary/destructive, content/fixed/fill, disabled | `d66806b` |
| 4 | Icon buttons | `iconButton` | Toolbar, titlebar and row actions, hover/pressed/disabled | Pending |
| 5 | Icon toggles | Active `iconButton` use | Selected state, persistence, tooltip and click ownership | Pending |
| 6 | Switches | `ToggleSwitch` | Form switches, toolbar Physics/Animation, disabled state | Pending |
| 7 | Checkboxes | Settings and form checkboxes | Checkbox, label ownership, disabled and checked states | Pending |
| 8 | Text inputs | `GlassInput` | Plain, typed, invalid, disabled, focus, selection and commit/cancel | Pending |
| 9 | Search/filter pill | Picto searchable inputs | Clear action, empty/non-empty, focused, clipped, disabled | Pending |
| 10 | Dropdown | Settings `CmSelect` Sort By / Date Added | Closed/open, exact seven-option fixture, selected fill, separators, keyboard and dismissal | Pending |
| 11 | Slider | Picto slider use | White thumb, primary fill, readout, disabled, notches and precision | Pending |
| 12 | Numeric well | `InspectorField` numeric input | Typed edit, precision, modifiers, invalid text and commit/cancel | Pending |
| 13 | Axis-well row | Picto inspector row grammar | XYZ sizing, hinge-axis wrapping, labels, typed state and fixed allocation | Pending |
| 14 | Color well | Picto color trigger | RGB/RGBA, unavailable and disabled states | Pending |
| 15 | Color picker/palette | `ColorPalette` / `ColorPicker` | Open/close, actor tints, popup ownership and occlusion | Pending |
| 16 | Progress bar | Picto progress use | Determinate, busy and disabled states | Pending |
| 17 | Status/outcome row | Picto status use | Busy/result text, action slot reservation and cancellation | Pending |
| 18 | Segmented control | Picto segmented navigation | Content/fixed/fill, equal segments, selected and disabled | Pending |
| 19 | Primary workspace tabs | Picto workspace navigation | Pose/Animation/Appearance; no width, scroll, or rail reflow | Pending |
| 20 | Secondary content tabs | Picto compact tabs | Body/Face/Matrix/3D and comparable sub-navigation | Pending |
| 21 | Sidebar row | `SidebarRow` | Selection fill, disclosure, actions, text clipping and exact row ownership | Pending |
| 22 | Tree composition | `FolderTree` | Guides, categories, filtering, cascading state and disclosure persistence | Pending |
| 23 | Property/form row | `PropertyRow` / `InspectorField` | Labels, controls, readouts, unavailable state and wrapped help | Pending |
| 24 | Section/disclosure header | `InspectorSection` | Captions, separators, collapse state and exact content removal | Pending |
| 25 | Titlebar | `WindowControls` / app shell | Status, undo/redo, global actions, collapse and drag regions | Pending |
| 26 | Toolbar | Picto toolbar layouts | Sizing, grouping, right alignment, overflow and disabled state | Pending |
| 27 | Page action bar | Picto action layouts | Allocated fill, intrinsic actions, wrapping and stable reserved slots | Pending |
| 28 | Fixed footer | Picto footer layouts | Parenting/canvas footer, placement, separator, padding and centering | Pending |
| 29 | Sidebar status bar | Picto shell status | Counts, FPS, undo/redo, fixed placement and optical alignment | Pending |
| 30 | Scroll region | Picto scroll surfaces | Stable gutter, 20%-narrower thumb, padding symmetry, nested ownership | Pending |
| 31 | Context menu | `ContextMenu` | Rows, separators, icons, selected/disabled, outside click and Escape | Pending |
| 32 | Popover | Picto floating surface | Anchor/flip/clamp, dismissal, layering and no underlying interaction | Pending |
| 33 | Search picker | Picto searchable picker | Filtering, rows, overflow, selection and frozen target | Pending |
| 34 | Hover help | `KbdTooltip` | Delay, Picto transition, blur, edge placement and one-card ownership | Pending |
| 35 | Modal | `GlassModal` | Backdrop, focus, dismissal, action layout and no click-through | Pending |
| 36 | File dialog | Picto modal/file grammar | Header/list/footer, navigation, selection, scrolling and frozen target | Pending |
| 37 | Shell composition | `OverlayShell` and Picto app shell | Sidebar/main/inspector/status chrome, stable width, collapse and tab transitions | Pending |

Each handoff covers only one numbered row.

## Architecture and cleanup rules

- `Theme` is the only theme value; component code consumes it through canonical
  primitives and compositions.
- Picto's TypeScript/JS structure is an architectural reference as well as a
  visual reference. Preserve useful boundaries, variants, controlled-state
  semantics, and composition patterns when they make sense in immediate-mode UI.
- Translate semantics, not framework machinery. Do not introduce a React-style
  virtual tree, CSS engine, generic layout framework, or retained component graph.
- Public component APIs are concise, strongly typed, product-agnostic, and
  callback-based. Product names, actor/bone services, and pane state never enter
  Crystarium.
- Prefer a small value/style record and explicit variants over boolean piles,
  parallel overload families, or a kitchen-sink property bag. Invalid variant,
  width, height, or state combinations should be unrepresentable where practical.
- Callers own durable values and business state. The component owns only
  ephemeral interaction state such as hover, active press, editing, open
  transition, and keyboard navigation, keyed by stable ImGui identity.
- A component owns its complete visual and interaction geometry. Measurement,
  rendering, hit testing, focus, clipping, and disabled behavior use the same
  resolved bounds instead of recomputing competing rectangles.
- Composite components reuse normalized primitives. They may arrange or
  coordinate them, but must not redraw local approximations of those primitives.
- `UiWidth` and `UiHeight` remain type-safe. Unsupported width/height
  combinations must not compile or silently fall back.
- Crystarium is the public product authoring surface. Product panes do not call
  internal rendering recipes or ordinary raw ImGui widgets.
- Component state belongs to the component or the owning application session,
  not to a pane-local duplicate.
- Popup/modal ownership is globally ordered. A higher floating surface blocks
  draw, hover help, hit testing, and clicks beneath it.
- Composite components such as tabs, titlebars, toolbars, footers, form rows,
  scroll regions, and the shell are first-class catalog entries, not incidental
  groups of coordinates.
- Keep documentation limited to this PBI, the external review process exception,
  and the existing UI architecture invariant when a durable rule changes.

## Slice handoff format

```text
PBI-011 slice:
Base/head:
Commit(s), no amend/rebase:
Exact Picto source and selector:
Picto TSX API/state/event-flow findings:
Crystarium public API and state ownership:
Canonical component changed:
All migrated call sites:
Deleted competing paths:
Required states captured:
Automated reports:
Residual pixels and explanation:
Debug build:
git diff --check:
In-game locations for user inspection:
No visual/runtime claim:
```

## Completion gate

PBI-011 is complete only when every row has a user-accepted commit, the catalog
contains every retained ordinary primitive and composite, the full six-theme /
three-scale comparison run is generated, and static inspection finds:

- no pane-local ordinary component implementation;
- no duplicate component styling or popup ownership path;
- no raw ordinary ImGui controls outside Crystarium internals;
- no stale Picto fixture or unrelated reference component;
- no optional-state or top-level-navigation reflow;
- no unresolved geometry, color, state, flow, or interaction diff hidden by a
  threshold.

The final gate is one complete in-game pass over Pose, Animation, Appearance,
Settings, sidebar/tree, inspector, matrices/maps, all floating surfaces, and the
collapsed shell. Compilation and the comparison window support that decision;
they do not replace it.
