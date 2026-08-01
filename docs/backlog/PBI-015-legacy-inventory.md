# PBI-015 phase-0 legacy inventory (at `c71d682`)

Severance authority: a legacy surface is deleted only when every file listed
for it here has migrated (slice gate 5). Product consumers and
library-internal dependents are separate obligations — a surface with zero
remaining product consumers may still be load-bearing inside `Poser.UI`.
Paths are relative to `Poser/UI` (product) and `Poser.UI` (library) unless
rooted. `Crystarium.ActiveTheme` (140 uses) is a pervasive read-only theme
accessor across nearly all product files and is not itemized.

## Product owning surfaces (exact files)

| Legacy surface | Product consumers |
|---|---|
| FloatingMenu | Panes/AnimationPane.cs, UIManager.cs, Windows/MainWindow.cs |
| ActionBar | Panes/PoseInspectorPane.cs, Views/AppShellView.cs, Views/SettingsView.cs |
| ScrollRegion | Panes/PoseInspectorPane.cs, Views/AppShellView.cs, Views/SettingsView.cs, Windows/MainWindow.cs |
| ListRow | Controls/AnimationPicker.cs, Views/SettingsView.cs |
| SegmentedControl (+Measure) | Panes/PoseInspectorPane.cs, Views/AppShellView.cs |
| IconButton | Views/AppShellView.cs |
| TemporaryIconToggle | Views/AppShellView.cs |
| FileDialog | Panes/AppearancePane.cs, Panes/PoseFileInspectorSection.cs |
| Page / PageScope / FormScope / Section | Panes/AnimationPane.cs, Panes/AppearancePane.cs, Panes/ExpressionInspectorSection.cs, Panes/PoseFileInspectorSection.cs, Panes/PoseInspectorPane.cs, Views/SettingsView.cs |
| FloatingSurface | ../Poser.cs, Views/AppShellView.cs, Views/SettingsView.cs, Windows/SkeletonOverlayWindow.cs |
| Modal | Windows/MainWindow.cs |
| Popover / OpenPopover / PopoverScope | Controls/AnimationPicker.cs |
| SearchPicker | Panes/AppearancePane.cs |
| HoverHelp | Panes/GraphicalBonePane.cs, Panes/PoseInspectorPane.cs, Panes/PoseRailPane.cs, UIManager.cs, Views/BoneMatrixView.cs, Windows/SkeletonOverlayWindow.cs |
| Dropdown | Panes/PoseFileInspectorSection.cs, Panes/PoseInspectorPane.cs |
| TextInput | Windows/MainWindow.cs |
| Button (+MeasureButton) | Panes/PoseInspectorPane.cs, Panes/PoseRailPane.cs, Windows/MainWindow.cs |
| TextAt | Panes/PoseInspectorPane.cs, Panes/PoseRailPane.cs, Views/AppShellView.cs, Views/BoneMatrixView.cs, Windows/MainWindow.cs |
| Text | Panes/PoseInspectorPane.cs |
| MeasureText | Panes/PoseRailPane.cs, Views/AppShellView.cs, Views/BoneMatrixView.cs |
| Icon | Panes/PoseRailPane.cs, Views/AppShellView.cs |
| FilterPill | Panes/PoseInspectorPane.cs, Views/AppShellView.cs |
| UseTheme | ThemeSelection.cs |
| CancelAxisEdit | Views/AppShellView.cs |

Leaf controls not listed (Switch, Checkbox, Slider, AxisWell, ColorWell,
Swatch) have no direct product call sites; they are reached only through the
Page/Form scopes above, so their severance follows those scopes' consumers.

## Library-internal dependents (Poser.UI, excluding self)

| Legacy surface | Library dependents |
|---|---|
| FloatingMenu | Primitives/Tags/ContextMenu.cs |
| ScrollRegion | Compositions/FileDialog.cs, Compositions/FloatingSurface.cs, Compositions/SearchPicker.cs, Primitives/Tags/Dropdown.cs, Primitives/Tags/Popover.cs |
| ListRow | Compositions/FileDialog.cs, Compositions/FloatingSurface.cs, Compositions/ScrollRegion.cs, Compositions/SearchPicker.cs |
| SegmentedControl | Compositions/PageForm.cs, Primitives/Tags/Popover.cs |
| IconButton | Compositions/ActionBar.cs, Compositions/FileDialog.cs, Compositions/FloatingSurface.cs, Primitives/ControlStyle.cs, Primitives/Tags/Button.cs |
| FileDialog | Rendering/Theme.cs |
| TemporaryIconToggle | Primitives/Tags/Button.cs |
| PageForm | Primitives/Tags/ColorWell.cs |
| Modal | Primitives/Tags/Popover.cs, Rendering/Internal/Interactive.cs |
| Popover | Compositions/SearchPicker.cs |
| HoverHelp | Compositions/PageForm.cs, Rendering/Internal/Interactive.cs, Rendering/Theme.cs, and every leaf control tag (AxisWell, Button, Checkbox, ColorWell, Dropdown, SegmentedControl, Slider, Switch, TextInput) |

## Raw-ImGui files (NativeHost / escape-hatch inventory)

Ordinary layout/controls in these files migrate to components; named escape
hatches (gizmo/canvas geometry, IME text editing, window lifecycle) remain:

Controls/AnimationPicker.cs, Controls/RotationGizmoRings.cs (canvas),
Controls/WorldGizmo.cs (canvas), Panes/AnimationPane.cs,
Panes/GraphicalBonePane.cs (canvas), Panes/PoseInspectorPane.cs,
Panes/PoseRailPane.cs, UIManager.cs (window lifecycle), Views/AppShellView.cs,
Views/BoneMatrixView.cs, Views/SettingsView.cs,
Windows/GizmoOverlayWindow.cs (canvas), Windows/MainWindow.cs,
Windows/SettingsWindow.cs, Windows/SkeletonOverlayWindow.cs (canvas).

## Accepted baseline accounting (tools/count-lines.ps1 semantics)

- production handwritten: 22,120 (non-blank lines; the PBI prose figure
  23,756 was `wc -l` including blanks — this script is the canonical
  measure from phase 0 on)
- tooling source: 5,738 (adds handwritten .html/.js/.json to the earlier
  4,953 .cs/.py/.ps1-only figure; Markdown docs excluded by design)
- accepted catalog: 71 states, hashes in
  `tools/ui-conformance/accepted-c71d682-hashes.txt`, enforced by
  `tools/ui-conformance/verify-accepted-hashes.ps1` (fails on any added,
  missing, or changed state)
