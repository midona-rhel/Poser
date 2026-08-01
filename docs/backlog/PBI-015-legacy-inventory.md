# PBI-015 phase-0 legacy inventory (at `c71d682`)

Frozen legacy surface: every entry below is deleted only when its final
consumer migrates (slice gates 5). Counts are product-side call sites in
`Poser/UI` + plugin root; leaf controls consumed through composition scopes
are owned by those compositions' consumers.

## Direct product consumers of the legacy Crystarium API

| Legacy surface | Product call sites | Final owning surfaces |
|---|---|---|
| FloatingMenu | 14 | MainWindow menus (sidebar-add, actor, bone), AnimationPane |
| ActionBar | 10 | SettingsView |
| ScrollRegion / ListRow | 8 | AppShellView tree, pickers, FileDialog list |
| SegmentedControl | 6 | AppShellView mode header |
| IconButton | 4 | AppShellView shell actions, ActionBar builder, FileDialog, FloatingSurface close |
| FileDialog | 4 | pose/appearance import-export flows |
| TemporaryIconToggle | 2 | AppShellView (28-box armature; 20-box row toggles) |
| PageForm (Page/Section/Form rows) | pervasive | SettingsView, AppearancePane, inspector panes — leaf controls (Dropdown, TextInput, Switch, Checkbox, Slider, AxisWell, ColorWell/Swatch, Text) are reached through its scopes |
| Modal / Popover / SearchPicker / HoverHelp | via compositions | dialogs and pickers; HoverHelp is registered by every control |

## Raw-ImGui files (NativeHost / escape-hatch inventory)

Ordinary layout/controls in these files migrate to components; named escape
hatches (gizmo/canvas geometry, IME text editing, window lifecycle) remain:

AnimationPicker, RotationGizmoRings (canvas), WorldGizmo (canvas),
AnimationPane, GraphicalBonePane (canvas), PoseInspectorPane, PoseRailPane,
UIManager (window lifecycle), AppShellView, BoneMatrixView, SettingsView,
GizmoOverlayWindow (canvas), MainWindow, SettingsWindow,
SkeletonOverlayWindow (canvas).

## Accepted baseline accounting (tools/count-lines.ps1 semantics)

- production handwritten: 22,120 (non-blank lines; the PBI prose figure
  23,756 was `wc -l` including blanks — this script is the canonical
  measure from phase 0 on)
- tooling handwritten: 4,953
- accepted catalog: 71 states, hashes in
  `tools/ui-conformance/accepted-c71d682-hashes.txt`
