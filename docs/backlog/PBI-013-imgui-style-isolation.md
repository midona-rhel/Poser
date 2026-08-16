# PBI-013 — Exception-safe ImGui style isolation

## Control

| Field | Value |
|---|---|
| Status | Ready after the active branch is clean; implement separately from PBI-012 |
| Size | Medium, urgent cross-plugin defect |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | Record from the clean accepted head before implementation |
| Feature branch | `feature/pbi-013-imgui-style-isolation` |
| Accepted head | Pending |

## Defect

Poser styling can escape its own windows and change other Dalamud plugin
windows. Rounded corners are the confirmed symptom; colors, padding, borders,
fonts, and spacing share the same risk.

`MainWindow.PreDraw` pushes global ImGui colors plus `WindowPadding`,
`WindowBorderSize`, and `WindowRounding`, while `PostDraw` pops them. A thrown
`Draw`—including the PBI-012 pose-commit exception—can leave that shared stack
active if normal post-draw cleanup is skipped. `Crystarium.View` uses the same
split lifecycle. Local primitives also contain raw push/callback/pop sequences,
and `Modal` directly overwrites `ImGui.GetStyle().Colors[ModalWindowDimBg]`
without restoring the incoming value.

## Outcome

Every Poser draw is a transaction over shared ImGui state. On normal return,
early return, closed popup, callback failure, or thrown exception, the complete
incoming style and stack state is restored before another plugin draws.
Poser retains its current visual design; other plugins never inherit Poser
rounding or any other theme value.

## Isolation contract

- Global ImGui state is borrowed, never installed. Poser may push scoped values
  while drawing its own surface and must restore the exact incoming values.
- Cleanup is exception-safe and idempotent. A recovery path and normal
  `PostDraw` may both execute without double-pop.
- Window-level styling that begins in `PreDraw` has a failure cleanup path
  inside `Draw`; normal cleanup remains at the correct end of the window.
- A helper that invokes product or caller content owns a `finally` around every
  style, color, font, disabled, width, clip, group, and ID stack it opens.
- Direct writes through `ImGui.GetStyle()` are prohibited unless the exact
  previous value is captured and restored in the same bounded draw operation.
- Poser must not reset ImGui to assumed defaults. Restoration always uses the
  state that existed immediately before Poser drew so user and other-plugin
  themes remain intact.
- Input ownership (`WantCaptureMouse`) and Poser's internal `Theme` selection
  are separate contracts and are not changed by this PBI.

## Implementation requirements

1. Introduce one internal exception-safe scope/ledger for Poser-owned ImGui
   pushes. It records only what it owns, unwinds in reverse order, and tolerates
   an already-completed cleanup. Prefer retained Dalamud RAII helpers where they
   provide the same guarantee; do not create a second UI framework.
2. Migrate the window-level theme scopes in `MainWindow` and
   `Crystarium.View`. A `Draw` exception must unwind the `PreDraw` pushes before
   it propagates to Dalamud, while a normal frame pops exactly once.
3. Audit and migrate every raw push/pop pair in `Poser` and `Poser.UI`,
   prioritizing `FloatingSurface`, `Modal`, `ScrollRegion`, `Dropdown`,
   `AxisWell`, `TextInput`, settings, overlays, and panes. Content callbacks may
   throw at any point without leaking their surrounding state.
4. Remove the persistent `ModalWindowDimBg` mutation. Implement the backdrop
   with a genuinely scoped ImGui value or Poser's owned modal draw geometry,
   then restore the incoming color exactly.
5. Do not swallow the original draw exception. Log its surface and operation
   once, restore shared state, and preserve the original stack/exception for
   diagnosis.
6. Add a development-only style-isolation probe to the existing in-game test
   path. It fingerprints all exposed ImGui style scalars/vectors/colors before
   and after a Poser draw, including a controlled exception after window styles
   are pushed. It must report the exact changed field and before/after values.
   Remove or compile-gate the deliberate throw trigger from production UI.
7. Add one durable invariant to `docs/architecture/ui-workspace.md`; do not
   create class-by-class styling documentation.

## Static review gates

- No unscoped assignment to `ImGui.GetStyle()` fields or color entries.
- No raw push followed by a callback and a later pop without `finally`/RAII.
- No window class relies solely on `PostDraw` to unwind state established in
  `PreDraw`.
- Every pop count is owned by the same scope that performed the pushes.
- Release validation and `git diff --check` pass. Debug is deployment-only;
  report existing unrelated warnings
  honestly.

## In-game acceptance

1. Open a second Dalamud plugin window with visibly different square/rounded
   corners. Record its normal appearance.
2. Open, close, collapse, and switch Poser tabs. The second window's rounding,
   padding, borders, colors, text, and scrollbar styling never change.
3. Exercise the Poser plus menu, actor/bone context menus, dropdown, color
   picker, file dialog, modal, settings, and nested scroll regions. Test normal
   close, outside click, Escape, selection change, and Poser window close.
4. Run the controlled exception probe after the MainWindow style push. Poser
   reports the injected failure; the second plugin window and Poser on the next
   frame are unchanged.
5. Run the probe while each callback-bearing floating surface is open. Every
   before/after fingerprint is identical and no ImGui stack assertion appears.
6. Repeat the normal and exception cases for 100 frames at 100%, 125%, and 150%
   UI scale. No cumulative rounding or style drift is allowed.
7. Disable/unload Poser while a popup or modal is open. Shared ImGui styling
   returns to the exact pre-Poser state.

## Excluded

- No redesign or normalization of Poser component visuals.
- No changes to another plugin or to global Dalamud style defaults.
- No replacement CSS/layout engine, generic test framework, DevHost, IPC click
  simulation, or screenshot comparison.
- No bundling with the PBI-012 pose-layer fix or PBI-011 component slices.

## Handoff

Report the exact base/head range, every migrated style owner, deleted direct
mutation paths, exception-unwind design, probe output for normal and injected
failure frames, Release validation, `git diff --check`, and remaining in-game
checks.
Compilation does not prove cross-plugin isolation.
