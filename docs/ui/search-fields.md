# Search fields

## Purpose

Poser uses one shared search-field presentation for every live filter. The
sidebar actor/bone filter and pose-workspace searches query different datasets, but they
are the same `Crystarium.TextInput` component with `Clearable = true`.

## Shared clear action

When a clearable input contains text, `Crystarium.TextInput` renders a trailing
14 px translucent foreground circle with the standard 9 px `TablerIcon.X`
centered inside it. The X uses the input's dark surface color to produce the
cut-out treatment. The visual circle has an 18 px pointer target and brighter
hover feedback.

Activating the action assigns an empty string, reports the input as changed, and
invokes the same `OnChange` callback used by typing. It is absent for empty or
disabled fields. Rendering preserves the input's normal layout cursor and
captures focus/hover state before drawing the trailing icon.

## Shared filter pill

`Crystarium.FilterPill` is the compact collection-filter primitive. It wraps
`TextInput` with the approved 26 px pill height, placeholder behavior, and the
same clear affordance. The caller owns only the ID, bound value, placeholder,
and available width.

The scene tree and Pose Matrix use this primitive. A pane must not recreate the
same filter by assembling a bare input and a separate clear button; doing so
causes the geometry, clear icon, and keyboard behavior to drift. Contextual
spacing remains the pane's responsibility: Matrix keeps 12 px above and below
the pill while the sidebar uses its navigation inset.

## Filter domains

- `AppShellView.SidebarSearch` filters actors, bone categories, bones, objects,
  cameras, and lights while preserving their visible hierarchy.
- `SpawnBrowserViewModel.Search` filters spawnable game-data rows and enables
  raw world model/VFX path results for queries of at least three characters.

Placeholder copy communicates the domain; height, inset surface, typography,
focus border, clear action, and interaction remain identical.

## Verification

- Empty fields show no trailing action.
- Typing into either field reveals the same clear control.
- Clicking the control clears immediately and restores the full result set.
- The action remains centered and unclipped at supported UI scales.
- Long queries remain editable even when their final characters scroll beneath
  the trailing action.
