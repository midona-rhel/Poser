# UI workspace

`UiWindowSet` keeps the workspace windows and their draw order: the main
window, settings, spawn browser, gizmo and skeleton overlays, detached sidebar
and toolbar parts, frame profiler, and reference pictures.
The skeleton overlay starts enabled for each session. Selected bones remain
visible as anchors when the global overlay mask hides other bones.

Reference pictures are part of the product. Each picture keeps its visibility,
opacity, placement, and window. It can be duplicated. A hidden picture stays
hidden as the workspace opens and closes, and a dismissed window is removed
after its draw pass.

The UI owns filters, disclosure, hover, picker state, formatting, and widget
interaction. It does not own selection, game baselines, pose accumulation,
undo, or entity identity. Rows carry stable ids and use the current viewport
for positions. Expanding a tree does not change selection.

Form section headers start expanded. Their disclosure preferences persist in
configuration under stable page/section keys, independent of actors, entity ids,
and attached/detached hosts. Reopening a window or restarting does not reset them.
Settings categories have separate keys even when section titles match. Search
temporarily reveals matching content without changing saved disclosure.
Inspector rail headers use this same contract, including IK, Gaze, Expression,
Pose, Tracking and Placement, whether attached or detached.

New explanatory or advisory text placed in a UI layout requires explicit user
approval before implementation. Do not add free-standing notices as a substitute
for correct behavior; propose the message and placement first.

Numeric, text and colour controls use the shared value-edit and commit events;
the [journal contract](../features/selection-and-transforms.md#the-journal)
defines when those edits enter history.

Keep IK gizmo visible is enabled by default and exempts an enabled IK endpoint
from the manipulation fade. It does not override explicitly hidden entity handles
or Alt suspension. Other gizmos retain the ordinary hide-while-manipulating rule.

UI → Visibility → Hide handles by default starts off and persists across restarts.
Untouched world handles and their gizmos follow this default; explicit per-entity
or group Show/Hide choices take precedence for the session. Selection does not
reveal a hidden handle or gizmo. Inspector controls and skeleton/bone visibility
are independent and remain usable.

Entity save-to-file, save-to-library, and category-wide destruction live under
More in the inspector or entity context menu, only where supported. A lone
save action stays a direct button/menu item instead of a one-item More menu. More uses
the three-dot icon and precedes the final Destroy/Delete/Release action. Category-wide
destruction (lights, cameras, objects, and overlays) opens a modal with the target count
and what is released or protected; only its explicit confirmation runs the
captured operation. Cancel, Escape, and closing the modal do nothing. Single
entity actions and automatic session teardown retain their existing routes.

Context menus use the clicked target; only right-clicking a member of an
existing multi-selection offers selection-wide commands. Opening another
target replaces the old menu. Removal drops that target from selection, not
unrelated entities. Library list rebuilds dismiss index-based menus.
Equivalent commands share labels and current capability gates; a changed
menu shape dismisses stale rows rather than dispatching their old indices.

Tree disclosure belongs to the clicked branch, including descendants that
have not been drawn yet. Search temporarily reveals matches and disables
disclosure commands. Skeleton/weapon-slot menus identify their own bones;
whole-actor presets and pose commands are explicitly labelled Actor, while
Skeleton settings opens the shared settings page. Shared sections reuse the
existing commands, not a second settings or pose state.

The shell owns the content origin, inset, scrollbar gutter, and content mode.
Panes use the content box they receive. Scroll position includes both the
active strip and tab. Detaching the sidebar or toolbar leaves the attached
content and inspector geometry in place. Collapse leaves the title bar.

The sidebar and inspector each retain their last detached position and expanded
size in configuration. First detach seats a panel where it was attached;
reattaching does not overwrite its detached placement. Subsequent detach and
restart restore that placement. Geometry is saved after a move/resize finishes,
not on every drag frame, and title-bar collapse does not replace the expanded size.

The separate toolbar has its own session-only compact state. Double-click
the Poser/GPose brand/status region to show only that content and shrink the
toolbar horizontally; repeat to restore its content-sized width. Single-click
and drag still move the toolbar. Hidden actions are not drawn or interactive.
Main-window collapse, detached panes and their geometry are unaffected.

Shared file dialogs keep a fixed search row above the file-list column. The
search glyph aligns with the row glyphs, independently of input padding.
Search filters cached current-directory names case-insensitively, including
folders, after extension filtering; it never walks subfolders or rescans per
keystroke. Navigation and reopening clear the query. A filtered-out selection
clears its preview and confirmation state; a separately typed save name stays.

Spawn browsing keeps predefined actions first, library entries second, then
the existing catalog order. With an active search, each source group orders
categories as actors, lights, cameras, props, scenery, furniture, VFX, overlays.
Action order stays fixed within its category; other matches rank name prefixes
then alphabetically within each category. Sorting changes visible row indices,
not the backing activation identities. Furniture has its own import actions;
world-entry library filters distinguish scenery, furniture and VFX despite
their shared file extension.

Pages default to a readable content-width cap. Actor opts into the
shared responsive policy: it fills the host's content box after the leading
inset, retains the host's trailing scrollbar gutter, and wraps cell groups
when each label/control track would become too narrow. Paired rows stack at
their shared minimum width. Fixed-size icons stay fixed within those tracks;
text-oriented pages retain their existing width policy.

A sequence of peer controls uses a responsive grid when hardcoded semantic
row breaks would leave uneven or wasted space. This does not flatten content
whose pairing or hierarchy carries meaning. The grid preserves source and
reading order. Each label and its controls or actions form one indivisible
group; an action never wraps away from the value it owns.

The set's common intrinsic minimum is its widest label, the label gap, and its
fixed or intrinsic controls. The actual pane content box determines the largest
column count that can hold that minimum, and equal tracks distribute the groups.
Changing width adds or drops columns; a narrow layout becomes one whole group.
The final row contains only real entries, with no invisible fillers, reserved
empty cells, or semantic partitions that strand one item.

Icons, swatches, and compact actions keep their natural size. Extra width goes
to tracks and spacing instead of stretching them into bars. Labels truncate
only after the whole group can no longer retain its natural label width. The
owning page provides scrolling; a repeated-control grid does not add an inner
scroll merely to preserve a preferred column count. Appearance Colours and
Custom colours are the current example of this shared pattern.

Responsive-grid audits check normal, narrow, and wide panes in game for order,
wrapping, clipping or overlap, attached actions, and unused space. Visual
acceptance remains manual under the [testing contract](../process/testing.md).

Crystarium and Picto are first-party UI work. Crystarium supplies the shared
controls, text, icons, placement, scrolling, and motion. `Interactive.Reserve`
owns hit testing, keyboard activation, pointer ownership, occlusion, and drag
completion. A drag ends once; a swallowed press has no drag end. Popovers,
menus, and floating surfaces use the same input chain.

Diagnostics stay with the surface whose state they describe. Completed actions
use `UserNotices`, while visible state changes do not need a second success
message. The workspace shows the current application state and operation
results.
Contract tests cover the current selection, layout, scrolling, input, gizmo,
picker, preview, pose, MCDF, scene, and lifecycle boundaries. In-game Poser is
the visual check for manual acceptance.
