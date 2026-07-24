# Pose workspace

## Purpose

The pose workspace is the main content and inspector projection for actor and
bone posing.

## Modes

- **Body** draws a deterministic body/armor/hand/tail/toe map.
- **Face** draws the race-appropriate facial map.
- **Matrix** shows categorized bones in a searchable matrix.
- **3D** uses the viewport skeleton canvas for direct selection.

Body and Face are rendered by `GraphicalBonePane`. The pane owns texture
loading, hit testing, mirrored placement, and marquee selection. It is embedded
content and cannot be opened independently.

## Commands

Transform controls begin a frozen-baseline application gesture, preview
absolute results from that snapshot, and commit one history patch. File,
expression, reset, mirror, and flip actions enter through focused application
use cases or temporary runtime facades while migration is incomplete.

## Inspector

`PoseInspectorPane` selects actor or bone controls from the shared selection.
`PoseRailPane` holds fixed supporting controls and mode-specific footer actions.
`InspectorLayout` is the single spacing, axis-row, and section grammar.

The workspace does not own camera, lighting, environment, appearance, status,
VFX, library, reference-image, or animation-browser content.
