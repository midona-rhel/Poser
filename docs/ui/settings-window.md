# Settings window

## Purpose

`SettingsWindow` is the only auxiliary product window. It edits configuration
used by the retained posing workflow and visual shell.

## Boundary

Settings is rendered by `SettingsView` and opened from the main title bar. It
does not host deferred feature managers. Settings that only configure removed
features are removed with those features rather than retained as disabled
controls.

The view paints its own chassis inside the transparent Dalamud window. It
shares the same theme, typography, glass, input, scrollbar, and icon primitives
as the main workspace. No standalone host or alternate rendering path exists.
