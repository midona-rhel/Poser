# Pose file inspector section

## Purpose

`PoseFileInspectorSection` owns selective pose import and export controls in
the Pose inspector rail. It isolates file-browser lifetime and import-option
state from live transform gestures.

## Responsibilities

- Own the Import and Export `FileBrowser` instances.
- Retain the last pose directory.
- Present the import scope as one compact **Scope** dropdown
  (Full default; Body, Expression, Selected).
- Present rotation, position, scale, descendant, and reset-before-import
  options.
- Build `PoseImportOptions`, including selected-bone filters from the
  application `SelectionSession` (canonical names of the selected bone ids).
- Invoke `IPoseFileService` for import and export.

It does not select actors or bones, manipulate transforms directly, or record
history. Those remain in the application/service layers used by the file
service.

## Host contract

`PoseInspectorPane` supplies the owning skeleton, origin, available width, and
UI scale. It calls `DrawBrowsers` once from the Pose content draw path so modal
file browsers remain available regardless of rail scrolling. The section
returns the exact height consumed by its inline controls.
