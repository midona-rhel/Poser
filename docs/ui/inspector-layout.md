# Inspector layout primitive

## Purpose

`InspectorLayout` is the single presentation primitive for the collapsible
sections in the retained Pose inspector. It owns only the repeated
`.insp/.prow` geometry and colors; the pose panes retain their state, controls,
callbacks, and service dependencies.

This boundary prevents every pane from carrying a private copy of the same
section hitbox, chevron, border, readable-width clamp, empty-state label, and
secondary-text colors.

## Contract

- Logical section height: 30 px.
- Expanded-body gap: 2 px.
- Optional top divider: 1 px at white 8%.
- Chevron center: `(2, 15)` from the section origin.
- Section label: 12 px semibold, white 50%, beginning at `(12, 9)`.
- Normal row label: white 50%.
- Hint/empty-state copy: white 40%.
- Emphasized value: white 90%.
- Inspector document width is capped at 660 logical px.
- Standard property rows are 30 px high with a 94 px label column.
- Float scrubbers begin 2 px below the row top and consume the remaining width.
- The caller supplies an ID prefix so disclosure hitboxes remain unique inside
  the shared main window.

`Section` owns the interactive hitbox and toggles the supplied state. `Header`
is the draw-only form used by product compositions that provide
their own fixed state. Both paths render the same geometry.

All logical values are multiplied by `ImGuiHelpers.GlobalScale` by the caller.
The helper does not start child windows, scroll regions, or columns and does
not modify persistent feature state beyond the supplied disclosure boolean.

## Ownership

`InspectorLayout` lives under `Poser/UI/Controls` because it is Poser's
product-specific inspector grammar. It is not a general Crystarium component:
Crystarium owns reusable widgets such as buttons, switches, segmented controls,
and scrubbers, while this helper composes those widgets into Poser's rail and
content-pane layout.

`AppShellView` owns the outer shell, sidebar, toolbar, scroll gutters, and
inspector rail dimensions. It no longer exposes section-drawing methods for
feature panes.

Every pane receives its registered services as required constructor
dependencies. A game hook or external capability may report `IsAvailable ==
false`, but a missing service object is a composition error rather than an
alternate UI mode. This keeps runtime capability checks distinct from
construction-time dependency checks and removes nullable branches from normal
draw code.

## Verification

Changes to this primitive affect every Pose inspector section at once. They
require:

1. a complete solution build;
2. user review in game;
3. a manual check of the actor and bone forms, including at least one collapsed
   and expanded section, at the active game UI scale.

The primitive must not be changed to make one pane look correct if that causes
another pane to diverge. Pane-specific spacing belongs in the pane body.
