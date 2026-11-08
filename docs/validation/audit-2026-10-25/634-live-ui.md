# Import UI inspection — 2026-10-25

Candidate: 07ca6a1f, deployed locally.
User accepted this deployed UI after the inspection.

Inspected the running Import menu and Import Pose file dialog using bridge screenshots and frame-scheduled pointer input.

- Both views showed aligned controls without visible overlap or clipping at the current game scale.
- Smart enabled: manual transform options disabled.
- Smart disabled: manual transform options enabled.
- Restored Smart enabled; the file dialog reflected that state.
- Opened From file and cancelled without importing a pose.
- The corrected Smart tooltip was checked in source previously, but automated hover did not display it; visual tooltip verification remains unconfirmed.

Screenshots: .tmp/import-menu.png, .tmp/import-manual.png, .tmp/import-file.png.
Native computer-use activation failed twice; bridge frame-scheduled input succeeded.
