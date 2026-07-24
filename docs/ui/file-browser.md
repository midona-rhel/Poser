# Pose file browser

## Purpose

`FileBrowser` is the retained in-plugin picker for pose import and export. It
is owned by `PoseFileInspectorSection`; it is not a general project/library
browser and does not persist scene state.

The picker has separate import and save instances, filters to the configured
pose extensions, tracks the current directory, and returns one selected path
through its completion callback. Closing or cancelling clears transient error
state.

## Presentation

`Modal` owns the ImGui popup lifetime and glass-styled window surface.
`Flex`/`FlexRow` is a narrow compatibility row collector used only to align the
path bar, optional save filename, and footer buttons through the shared
`Crystarium.Element` renderer. It owns no layout state after `Dispose`.

New product panels use the normal `Poser.UI` element primitives directly.
`FlexRow` is removed when the pose picker is converted to the service-free
view-model pattern used by the main and settings views.

## Boundary

The browser chooses a path only. Parsing, validation, scope options, and pose
application belong to `IPoseFileService` and the clean pose facades. The picker
must not write transforms or maintain a recent-file/library database.
