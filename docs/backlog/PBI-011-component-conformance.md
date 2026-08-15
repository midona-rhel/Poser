# PBI-011 — Component-by-component UI conformance

| Control | Value |
|---|---|
| Status | Historical — synthetic conformance workflow superseded (2026-08-12) |
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
Crystarium implementation and one Picto-derived behavioral contract. Product
panes compose those controls; they do not draw local variants. The entire
application supports the portable Picto themes
Dark, Light, Light Gray, Gray, Blue, and Purple. Acrylic, Mica, Liquid Glass, and
other platform materials remain out of scope.

Specialized posing canvases remain specialized: body/face maps, matrix content,
3D content, and gizmos are not converted into generic widgets. Their surrounding
tabs, toolbars, footers, scrolling, and input ownership are in scope.

## Disposition

The numbered capture, browser-reference, screenshot, pixel-diff, and hash-gate
directions below are historical and no longer executable. The synthetic
component catalog was not the Poser application and is retired. Future visual
acceptance compares the real current and rewritten in-game UI with a small
manual screenshot/video/action card; ordinary tests cover state, commands,
ownership, lifecycle, and layout invariants.

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
| 4 | Icon buttons | `iconButton` | Toolbar, titlebar and row actions, hover/pressed/disabled | Superseded -> PBI-014 (inherited unaccepted work checkpointed at `d27d232`) |
| 5 | Icon toggles | Active `iconButton` use | Selected state, persistence, tooltip and click ownership | Superseded -> PBI-014 |
| 6 | Switches | `ToggleSwitch` | Form switches, toolbar Physics/Animation, disabled state | Superseded -> PBI-014 |
| 7 | Checkboxes | Settings and form checkboxes | Checkbox, label ownership, disabled and checked states | Superseded -> PBI-014 |
| 8 | Text inputs | `GlassInput` | Plain, typed, invalid, disabled, focus, selection and commit/cancel | Superseded -> PBI-014 |
| 9 | Search/filter pill | Picto searchable inputs | Clear action, empty/non-empty, focused, clipped, disabled | Superseded -> PBI-014 |
| 10 | Dropdown | Settings `CmSelect` Sort By / Date Added | Closed/open, exact seven-option fixture, selected fill, separators, keyboard and dismissal | Superseded -> PBI-014 |
| 11 | Slider | Picto slider use | White thumb, primary fill, readout, disabled, notches and precision | Superseded -> PBI-014 |
| 12 | Numeric well | `InspectorField` numeric input | Typed edit, precision, modifiers, invalid text and commit/cancel | Superseded -> PBI-014 |
| 13 | Axis-well row | Picto inspector row grammar | XYZ sizing, hinge-axis wrapping, labels, typed state and fixed allocation | Superseded -> PBI-014 |
| 14 | Color well | Picto color trigger | RGB/RGBA, unavailable and disabled states | Superseded -> PBI-014 |
| 15 | Color picker/palette | `ColorPalette` / `ColorPicker` | Open/close, actor tints, popup ownership and occlusion | Superseded -> PBI-014 |
| 16 | Progress bar | Picto progress use | Determinate, busy and disabled states | Superseded -> PBI-014 |
| 17 | Status/outcome row | Picto status use | Busy/result text, action slot reservation and cancellation | Superseded -> PBI-014 |
| 18 | Segmented control | Picto segmented navigation | Content/fixed/fill, equal segments, selected and disabled | Superseded -> PBI-014 |
| 19 | Primary workspace tabs | Picto workspace navigation | Pose/Animation/Appearance; no width, scroll, or rail reflow | Superseded -> PBI-014 |
| 20 | Secondary content tabs | Picto compact tabs | Body/Face/Matrix/3D and comparable sub-navigation | Superseded -> PBI-014 |
| 21 | Sidebar row | `SidebarRow` | Selection fill, disclosure, actions, text clipping and exact row ownership | Superseded -> PBI-014 |
| 22 | Tree composition | `FolderTree` | Guides, categories, filtering, cascading state and disclosure persistence | Superseded -> PBI-014 |
| 23 | Property/form row | `PropertyRow` / `InspectorField` | Labels, controls, readouts, unavailable state and wrapped help | Superseded -> PBI-014 |
| 24 | Section/disclosure header | `InspectorSection` | Captions, separators, collapse state and exact content removal | Superseded -> PBI-014 |
| 25 | Titlebar | `WindowControls` / app shell | Status, undo/redo, global actions, collapse and drag regions | Superseded -> PBI-014 |
| 26 | Toolbar | Picto toolbar layouts | Sizing, grouping, right alignment, overflow and disabled state | Superseded -> PBI-014 |
| 27 | Page action bar | Picto action layouts | Allocated fill, intrinsic actions, wrapping and stable reserved slots | Superseded -> PBI-014 |
| 28 | Fixed footer | Picto footer layouts | Parenting/canvas footer, placement, separator, padding and centering | Superseded -> PBI-014 |
| 29 | Sidebar status bar | Picto shell status | Counts, FPS, undo/redo, fixed placement and optical alignment | Superseded -> PBI-014 |
| 30 | Scroll region | Picto scroll surfaces | Stable gutter, 20%-narrower thumb, padding symmetry, nested ownership | Superseded -> PBI-014 |
| 31 | Context menu | `ContextMenu` | Rows, separators, icons, selected/disabled, outside click and Escape | Superseded -> PBI-014 |
| 32 | Popover | Picto floating surface | Anchor/flip/clamp, dismissal, layering and no underlying interaction | Superseded -> PBI-014 |
| 33 | Search picker | Picto searchable picker | Filtering, rows, overflow, selection and frozen target | Superseded -> PBI-014 |
| 34 | Hover help | `KbdTooltip` | Delay, Picto transition, blur, edge placement and one-card ownership | Superseded -> PBI-014 |
| 35 | Modal | `GlassModal` | Backdrop, focus, dismissal, action layout and no click-through | Superseded -> PBI-014 |
| 36 | File dialog | Picto modal/file grammar | Header/list/footer, navigation, selection, scrolling and frozen target | Superseded -> PBI-014 |
| 37 | Shell composition | `OverlayShell` and Picto app shell | Sidebar/main/inspector/status chrome, stable width, collapse and tab transitions | Superseded -> PBI-014 |

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
Release validation:
git diff --check:
In-game locations for user inspection:
No visual/runtime claim:
```

## Closure (historical)

PBI-011 closed after slices 1–3 (Text `02d25f7`, Icons `b44a2f3`, Text buttons
`d66806b`) were accepted. Rows 4 onward were superseded by
**PBI-014 — Compact UI architecture** (`docs/backlog/PBI-014-compact-ui-architecture.md`):
the remaining controls are normalized during that structural migration and
accepted through its component-sheet catalog instead of pre-migration slices.
Icon-button work in flight at closure (commits `c853685..86ef855` plus the
worktree checkpointed at `d27d232`) was inherited by PBI-014 but never
separately accepted. The original completion gate below no longer applies;
its intent — no pane-local component implementations, no duplicate styling or
popup ownership paths, no raw ordinary ImGui outside Crystarium, and a final
in-game pass — carries forward as PBI-014 acceptance criteria.
