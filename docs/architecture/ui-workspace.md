# UI workspace

`Poser.UI` is everything the user sees: the windows, the tabs of the Properties panel, the sidebar and its menus, the skeleton and gizmo overlays drawn over the game, and the shared controls they are built from. It draws with Dalamud's ImGui. It does not touch the game: it reads from and sends commands to `Poser.Application` (see [product-and-boundaries.md](product-and-boundaries.md)).

## The folders

| Folder | What is in it |
|---|---|
| `Windows` | One class per window: the main window, Settings, the Library, the spawn browser, pop-out Properties windows, reference pictures, and the two overlays. |
| `Views` | What draws inside a window. The window gathers what to show and the view draws it. `AppShellView` is the main window's frame. |
| `Panes` | The pages and sections of the Properties panel, one class per subject: posing, animation, appearance, lights, cameras, environment, scene, the Library and the others. |
| `Composition` | The sidebar rows, the right-click menus and the removal dialog. |
| `Widgets` | The shared controls: buttons, sliders, text fields, menus, forms, file dialogs. |
| `Rendering`, `Primitives`, `Icons` | Drawing boxes, shadows, text and icons, and the colour themes. |

## How it is put together

`UIManager` hooks Dalamud's draw call and draws every window once per frame. `UiWindowSet` holds the windows and the order they are drawn in.

The main window and each pop-out Properties window get their own set of panes. `PresentationScope` in `Poser/Composition/PropertiesContentFactory.cs` creates them when the window opens and disposes them when it closes. Panes are never shared between windows, so each window keeps its own open tab, scroll position and pickers. All windows send their edits to the same commands and the same undo history. A pop-out keeps showing the thing it was opened for, even when the selection moves on. Pop-outs close when GPose ends.

The main window's frame owns the scroll area, the margin and the scrollbar gutter. A pane draws into the space it is given and does not set up its own.

## What the interface keeps

The interface keeps only how things are shown: search text, which rows and sections are expanded, hover, scroll position, open pickers and unsaved drafts in editors. Selection, undo history and anything about the game live in `Poser.Application` ([application-state.md](application-state.md)).

Some state that is not about drawing also lives here: which bones the skeleton overlay shows (`SkeletonOverlayPresentation`) and the bone-visibility presets, the reference pictures (`ReferenceImageSession`), the generated layout of the Body and Face maps (`GraphicalBonePane.Generated.cs`), and which shortcut runs which action (`PoserKeybinds`).

## Shared controls

Each family of controls is one static class in `Widgets` (`ButtonWidgets`, `SliderWidgets`, `PageForm`, `FloatingMenu` and so on). Call sites import them with `using static`. Their state, such as the active theme, text and texture caches, and which control is being dragged, lives in one `UiContext` object instead of in static fields. A plugin reload therefore starts clean.

`Interactive.Reserve` decides which control gets the mouse. Windows, menus, popovers, controls and the overlays all go through it, so a control under an open menu does not react to a click on the menu. Controls do not ask ImGui for hover or click state themselves.

Sliders, fields and colour pickers report when an edit is finished, which is what makes a drag one undo step (see [application-state.md](application-state.md#undo-and-redo)).

## Rules for changing the interface

- Dialogs are ordinary movable windows, not ImGui modal popups. They do not dim the game or block other windows.
- Messages about a finished action (a save, a refused import) go through `UserNotices`, which shows them as Dalamud notifications. A change the user can already see needs no message.
- Do not add explanatory or warning text to a layout. Fix the behaviour instead.
- Labels stay on one line and must fit at the smallest supported window size. Do not wrap them or make rows taller to hide a sizing problem.
- Interface changes are checked by looking at them in the game, not by unit tests (see [testing](../process/testing.md)).
