# App shell scrollbars and glass edge

## Purpose

`AppShellView` owns one scrollbar and edge-border contract for the main window's
three vertically scrollable regions: scene sidebar, main tab content, and the
inspector rail. Individual panes must not choose their own scrollbar widths or
inset the scroll viewport from the panel's right edge.

## Scrollbar treatment

The shell uses a 12 px transparent track (50% wider than the original 8 px
treatment) and a
4 px rounded, white-at-12% thumb (25% while hovered or active). The viewport
reaches the panel's outer-right edge, stopping one physical pixel before the
glass outline so the scrollbar cannot paint over it.

ImGui has no direct equivalent of CSS `scrollbar-gutter: stable` that reserves
space without also forcing a full-height thumb. The shell therefore gives each
pane a fixed content width that already excludes the 12 px gutter. Content width
does not change when overflow begins.

Horizontal balance is defined per surface:

- Sidebar: 12 px left inset; right side is 0 px gap + 12 px scrollbar.
- Main content: 12 px left inset; right side is the 12 px scrollbar gutter.
- Inspector: 12 px left inset; right side is 0 px gap + 12 px scrollbar.

In every case the content-to-scrollbar gap plus scrollbar width equals the
surface's left padding.

The main toolbar uses the same 12 px left and right inset as its content box.
Pose is a special viewport-owning pane: the shell's outer content child is fixed
and begins immediately below the toolbar; its scrolling middle surface inherits
this same scrollbar treatment. Ordinary document panes retain their 4 px top
gap. See [Pose surface layout](pose-surface-layout.md).

## Glass outline ordering

The sidebar and inspector paint opaque/translucent surface fills after the base
window chassis. If the asymmetric glass outline is painted only with the base,
those fills cover its left, right, and bottom edges. `AppShellView` repaints the
outer `Norvrandt.Box` after all panel fills using `Theme.Glass.BorderTop`,
`BorderSide`, and `BorderBottom`, preserving one continuous edge around the
main Pose surface.
