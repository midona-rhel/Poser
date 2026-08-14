# PBI-010 — Unified UI foundation and retained-workspace migration

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game after each slice |
| Base ref | `pbi-010-base` |
| Feature branch | `feature/pbi-010-unified-ui-foundation` |
| Accepted head | Not accepted |

## Outcome record (shortened 2026-08-14)

This PBI defined the one-ownership UI foundation: Theme owns every metric,
Crystarium is the single product-facing composition API, panes describe
content and callbacks without positioning ordinary widgets. The canonical
metric and composition contracts it tabulated now live in their normative
home, [architecture/ui-workspace.md](../architecture/ui-workspace.md); the
migration mechanics it planned were reshaped by the PBI-014 → PBI-015 →
PBI-016 chain (see those records).

The disposition of this document's remaining open items — including any
still-wanted "live correction tranche" entries — is a pending user decision;
nothing here should be executed as a plan without that call.
