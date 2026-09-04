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

The shell owns the content origin, inset, scrollbar gutter, and content mode.
Panes use the content box they receive. Scroll position includes both the
active strip and tab. Detaching the sidebar or toolbar leaves the attached
content and inspector geometry in place. Collapse leaves the title bar.

Pages default to a readable content-width cap. Appearance opts into the
shared responsive policy: it fills the host's content box after the leading
inset, retains the host's trailing scrollbar gutter, and wraps cell groups
when each label/control track would become too narrow. Paired rows stack at
their shared minimum width. Fixed-size icons stay fixed within those tracks;
text-oriented pages retain their existing width policy.

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
