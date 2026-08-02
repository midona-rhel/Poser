# PBI-015 — React-style component core over ImGui

| Control | Value |
|---|---|
| Status | Definition ready for Claude/Codex review; implementation not started |
| Size | Extra large, delivered through separately accepted vertical slices |
| Accepted SVG base | `c71d6822c00e869001666246e589c197ee604395` |
| Parked atlas experiment | `956d5824f7c65976786c12d32ddfd2b8f81806e7` |
| Planned feature branch | `feature/pbi-015-reactive-ui-core`, from the accepted SVG base |
| Implementation owner | Claude main loop; Opus 5 agents write implementation code |
| Review owner | Codex |
| Runtime and visual acceptance | User |
| Supersedes | PBI-014 phases 5–8 |

## Product and authoring contract

The UI rendered at the accepted phase-4 SVG baseline is the product contract.
Keep its pixels, flow, motion and behavior, using the component catalog at
`tools/ui-conformance/artifacts/index.html` as the visual oracle. Undo the
Chromium atlas direction: retain the accepted runtime SVG renderer. Its
roughly 1.3–1.6k lines are a better trade than generated texture coverage,
loader lifecycle and atlas memory.

The rewrite succeeds only if authoring ordinary UI feels like writing a small
Picto React component. A product author describes immutable props, children,
local state where needed, handlers and a reusable style. They do not know
about ImGui cursors, measuring, clipping, ID stacks, draw lists, pooling,
surface ownership or pixel snapping.

Picto uses React 18, Mantine 8, CSS Modules, Jotai/Zustand and Tabler — not
Material UI. This PBI borrows React's component/state model and MUI's typed
`sx`, variants and slots as an authoring vocabulary. The runtime stays C# and
ImGui; no JavaScript dependency or browser is added.

Concrete author-experience gates:

- a reusable stateless control is normally one props type, one static render
  function, and one small static style group;
- a stateful control differs only by an explicit typed state record;
- composition uses `Crystarium.Row`, `Column`, `Stack`, `Text`, `Svg`,
  `Button`, fragments and ordinary children;
- fixed children do not require hand-written ImGui IDs; dynamic/stateful
  siblings require meaningful keys;
- a basic clickable component must need no custom measure, paint or input
  code;
- migrated panes may not import ImGui for ordinary controls or layout.

## Branch and rollback boundary

Before implementation:

1. tag `c71d682` as `pbi-014-phase4-svg-baseline`;
2. tag `956d582` as `pbi-014-phase5-atlas-experiment`;
3. create the feature branch directly from `c71d682`;
4. carry this PBI onto that branch as the first docs-only commit.

Do not revert the atlas commits in place and do not carry their loader,
manifest or generated textures. The experiment remains available by tag.

## Runtime model: React semantics without a DOM

The system deliberately separates three concepts.

**Component.** Reusable rendering logic receiving immutable props and
returning declarations. A stateless component is a static function; it has no
class or retained instance. A stateful class component has one retained
instance and one typed state object inside its keyed component scope.

**Frame element.** REVISED 2026-08-02 (user decision, supersedes the tagged
Box/Text/SVG/Interactive/Native taxonomy): there is ONE element. Every
per-frame arena record carries the same optional facets — a stylesheet
reference plus inline patch, a child range, a typed listener set, leaf
content (text run or glyph), an optional floating surface, an optional
native island, a key. Interactivity is not a species: the PRESENCE of
listeners (or help) is what reserves a hit rect. Layout, iteration, state
resolution, painting, motion ramps, help and dispatch are implemented once
on this base; a control never reimplements any of them. Handles are
discarded together after the frame; records are pooled, never allocated
polymorphic objects. The container/interactable split was measured to be
wrong: every phase-1..3A wave paid a retrofit at that boundary (interactive
children, box painters, dispatch-mode bytes, painter-adapter classes that
decode untyped Arg conventions back into meaning), and the split is
deleted rather than patched further.

**Component scope.** A small retained record identified by root, parent
scope, component type and explicit key. It owns a stateful component instance,
its state, refs and typed adapter cleanup. It does not own a retained layout
or paint tree.

There is no DOM, virtual-DOM mutation, query selector, class registry,
stylesheet engine or general tree diff. Reconciliation matches component
identity and state only; layout and paint declarations are rebuilt cheaply
each frame.

## Stateless and stateful components

V1 supports functional/stateless composition and typed class state. It has
no hooks, named state-cell dictionary, positional ordering or generic effect
system.

```csharp
public static UiNode StatusPill(in StatusPillProps props) =>
    Crystarium.Row(/* declarations */);

public abstract class StatefulComponent<TProps, TState>
{
    protected abstract TState CreateState(in TProps props);
    protected abstract UiNode Render(in TProps props, in TState state);
    protected void SetState(Func<TState, TState> update);
}
```

`UiNode` is an opaque value handle into the current frame arena, not an
allocated object. `UiChildren` has a C# collection builder accepting a
`ReadOnlySpan<UiNode>`; small collection expressions therefore remain stack/
arena based. `UiNode.None` is skipped, making conditional children read as
`condition ? Child() : UiNode.None`. C# evaluates inner arguments before the
outer call, so children naturally enter the arena before their parent.

State updates are batched and visible next frame. Controlled values remain
the default: controls receive `Value` and `OnChange`, and props remain truth.
Local component state is limited to disclosure, popup openness, drafts and
picker-local selection. Scene, posing, selection, animation and appearance
remain in existing services; persisted preferences remain configuration.
Hover, press, focus, capture, motion and scroll offset belong to renderer or
host state, not arbitrary component state.

Every stateful component and dynamic sibling requires an explicit stable key.
Implicit ordinal identity is allowed only for stateless, unconditional host
children. Duplicate sibling keys and key/type reuse fail clearly in developer
builds. A skipped/collapsed root is suspended rather than spuriously
unmounted; unmount occurs only after a completed owning-root render proves
the keyed scope absent.

`UiKey` is a readonly, allocation-free tagged value supporting integer widths,
`Guid` and literal strings. Compound stable domain IDs use a type-discriminated
stable key factory; dynamic actor/bone rows never format strings for identity.

Typed `Ref<T>` exposes focus, scrolling and the committed arranged rectangle.
Refs become valid after commit and clear on unmount. Ordinary components own
no native resources; cleanup is restricted to typed NativeHost and Portal
adapters.

## Authoring shape

REVISED 2026-08-02 (user decision, supersedes the factory-method vocabulary):
a control IS its props. Every control is a readonly record struct whose
init-properties are its complete spec — `required` on what is mandatory,
everything else optional and falling through to its stylesheet — with ONE
implicit conversion to `UiNode` that writes the arena record. No factory
methods, no positional-argument forms, no Action/UiEvent overload pairs.

```csharp
new Button
{
    Label = "Hello World",
    Style = ButtonStyle.Danger,              // named preset -> theme sheet
    StyleSheet = new() { Width = 20, Height = 20, Opacity = 0.5f },
    OnClick = _onClickHandler,               // one UiHandler type, both worlds
}
```

Prop-bags compose directly in children (the implicit conversion feeds the
collection builder), and a control's whole "implementation" is its
conversion operator mapping props onto the one Element. Target: every
control at most HALF its pre-revision size; a control that needs a
painter-adapter class decoding untyped conventions (the SectionHeaderPainter
shape: `input.Arg != 0` meaning "expanded") is the named anti-pattern this
revision deletes.

Handlers stored in props are stable delegates created outside `Render`
(instance method groups allocate per frame in C# — hoist to fields).
`UiHandler` / `UiHandler<T>` wrap plain delegates and component
`UpdateState` tokens through implicit conversions; dispatch is typed and
implemented once on the base. Capturing lambdas created inside `Render`
remain forbidden and detected in developer diagnostics. `Help` is a
first-class prop on every element.

## Styling model

REVISED 2026-08-02 (user decision, supersedes the Sx patch vocabulary and
typed-slot styling): styling is ONE typed stylesheet record, `ElementSheet`.
Every visual and layout property an element can carry, every one optional —
a null field is NOT part of the spec, and resolution falls through
inline patch → active state sub-sheet → family sheet → inherited context →
renderer default. State looks are NESTED SUB-SHEETS (`Hover`, `Active`,
`Disabled`, `Selected`): sparse patches over the base, so "disabled
styling" is data the base resolves, never painter arithmetic in a control.
The theme declares one sheet per control family (`Theme.Styles.Button`,
`.ButtonDanger`, `.Row`, `.SectionHeader`, …); variants are sheets, built
with `with`-expressions at theme construction — the ButtonVariant enum and
per-painter palette switches die.

Sheets are immutable records allocated once when the theme is built; the
runtime never merges them. The walk flattens the chain into a small
resolved struct once per element per frame — allocation-free — and that
resolved value drives box paint, label paint, glyph tint, motion ramps and
child inheritance uniformly.

Rules that survive from the original model, unchanged in intent: fields are
added only for accepted controls or named product consumers; pseudo states
are paint-only (they may not change font metrics, border thickness,
padding, gap, alignment or size); state precedence is Disabled > Active >
Hover > Selected with named accepted exceptions (sidebar selection over
hover); typography and foreground inherit, box/spacing/size do not; glyph
tint defaults to the inherited foreground (currentColor); motion is opt-in
per paint property via the sheet's `Transition`, delegating to the accepted
keyed Motion store and preserving the existing midpoint fixtures; the
accepted disabled recipes are expressed as sheet data (`GroupOpacity` for
the compensated group fade, `Opacity` for flat fades) and implemented once
by the base painter. Theme tokens are not rewritten wholesale — sheets
adapt the accepted `Theme` values.

## Layout and flow

V1 implements only the HTML-like box behavior Poser actually needs:

- border-box content, fixed and fill sizing with min/max constraints;
- row, column and in-rect stack;
- padding, margin and gap, with no margin collapsing or auto margins;
- start, center, end and stretch alignment;
- deterministic fill of remaining space and deterministic overflow;
- explicit form/column tracks when the Appearance slice requires them;
- clipping, scrolling and stable gutters when a real migrated surface needs
  them. Gutter rule (user decision, 2026-08-02): every scrollable view insets
  its content by the scrollbar gutter width on BOTH sides — the right inset
  is padding or the bar itself, and the bar appearing or disappearing never
  reflows content. The accepted Page inset (12 = gutter width) already
  satisfies it; new reactive surfaces state it explicitly;
- portal-owned anchoring and surface order, not generic z-index/positioning.

No CSS Grid, wrap algorithm, float, table layout, selector cascade,
percentage layout, generalized transform or browser shrink algorithm is
implemented without a named consumer. Canvas/gizmo code keeps explicit
coordinates inside its escape hatch.

The frame pipeline is:

```text
build component declarations
→ reconcile component scopes
→ resolve metric styles
→ measure intrinsic content under constraints
→ arrange authoritative border/content rectangles and shared rounding
→ traverse arranged nodes, submitting absolute ImGui items in order
→ collect interaction, resolve paint-only state and paint
→ commit state, refs and proven unmounts
```

Intrinsic measurement uses font metrics, SVG view boxes and image sizes. It
never renders components twice or off-screen to discover size. Layout, clip,
hit testing, paint, HoverHelp and resize notification use the same arranged
rectangle. Logical units become scaled/snapped pixels centrally; parents own
rounding distribution so sibling drift cannot accumulate. Accepted
2026-08-02 (user decision, phase-1 review): centrally snapped edges are the
geometry contract at every scale — at fractional scales this deliberately
diverges sub-pixel from the legacy fractional-rect draw (equivalent against
the Picto oracle: 3.208% vs 3.217% average significant at 125%), and text
keeps its single snapping owner (Optical.Snap) by painting at the unrounded
position.

## Interaction, focus and portals

The first router exposes only behavior required by current controls:

- click/double-click and release-inside activation;
- pointer enter/leave and down/up;
- focus/blur and Enter/Space activation;
- drag begin/update/end with pointer capture;
- bubbling to logical parents with `StopPropagation`.

General capture-phase events, arbitrary key/scroll dispatch and DOM-style
event APIs are deferred until a named consumer requires them. The accepted
`Interactive`, `Motion`, surface ownership and occlusion code begins as the
private backend; it is not rewritten simultaneously with the authoring model.

Menus, dropdowns, tooltips, popovers and modals use portals. A portal changes
the visual surface but retains component identity, theme and logical ancestry.
The existing surface stack owns topmost keyboard/pointer access, dismissal,
clamping and drawing order. Portal state changes appear on the next frame.

Named native escape hatches are text editing/IME, ImGui window/popup
lifecycle, slider pointer-to-value math, color-picker internals, and custom
matrix/gizmo/overlay canvases. Each receives arranged rectangles and returns
events/value changes; it may not own surrounding layout or component chrome.

## Rendering and SVG

Retain FontRegistry/Text, BoxRenderer, ColorEx, GlassChrome, ControlPaint and
the runtime SVG pipeline/document cache/source registries from `c71d682`.
`SvgNode` delegates to a thin `SvgPainter`; controls do not parse, fit, tint
or cache SVG independently. The Chromium atlas prototype remains a tagged
experiment only.

## Source boundaries

```text
Core/       frame arena, tagged nodes, roots, keys and component scopes
State/      typed stateful instances, queued updates and refs
Style/      contained styles, patches, recipes and resolution
Layout/     constraints, box measure/arrange, row/column/stack/tracks
Input/      behavior, interaction adapter, focus, capture and ownership
Paint/      accepted box/text/SVG/image painters
Portal/     portal adapter and surface host
Hosts/      native ImGui and canvas escape hatches
Components/ thin reusable controls with typed props, variants and slots
```

No reflection, runtime CSS, class-name registry, selector engine, LINQ in hot
rendering, retained general DOM or partial-class dumping. Ordinary warm frames
must have negligible managed allocation. Roughly 300–400 lines is a review
trigger, not a mechanical split rule: each file must own one independently
nameable responsibility.

## Migration and severance

The accepted static UI is frozen: no new product consumers. A one-way
`UiRoot.Render(origin, size, build)` hosts the new tree inside an unmigrated
pane. `NativeHost` and `LegacyHost` are named temporary boundaries with an
inventory of their remaining owning surfaces; they gain no new consumers.

Old controls are deleted when their final legacy surface migrates, not before.
Every slice records the exact consumer inventory. No product feature logic is
rewritten merely to migrate presentation.

## Delivery phases

### 0. Clean base and accounting

Create both tags and the SVG-based branch, carry this definition, record the
accepted catalog hashes, inventory legacy consumers/adapters and commit one
reproducible handwritten production/tooling line-count command.

### 1. Minimal spine and Button proof

Implement the frame arena, tagged nodes, retained stateless/stateful component
scopes with typed state, minimal style resolution, Box/Text/SVG, row/column/
stack, `UiRoot`, and adapters to accepted input/motion/paint sufficient for
Button. Show old accepted Button and new Button through the existing visible
Picto | Crystarium | red-diff workflow; prove real release-inside, keyboard,
disabled and per-property motion behavior. Prove `UiNode`/children/style/event
construction adds no managed allocation on a warm frame, including the local
state reducer path. Stop for API/architecture review. Target no more than
roughly 1.8k new handwritten production lines.

### 2. Dropdown state and portal proof

Add only the state, ref, portal, overflow and focus behavior Dropdown proves
necessary. Its visible sheet contains all relevant states. Do not add a
general DOM event model or generalized scrolling.

### 3. First complete surfaces — Appearance, then Settings (two checkpoints)

Accepted 2026-08-02 (user decision): phase 3 is a two-step checkpoint so the
net-negative gate is measured on real diffs, not estimates. 3A — build only
what Appearance consumes (Page/Section/form rows and their tracks; Slider,
Switch, ColorWell, Progress twins; a portal SearchPicker), keep FileDialog
and the raw ColorPicker4 interior as named legacy/native boundaries, add the
missing Slider/ColorWell/Progress/SearchPicker comparison states, migrate
Appearance fully, delete its sole-consumer paths (SearchPicker, the Selector
and Progress row members, ProgressBar), and stop for code, comparison-window
and in-game review; this intermediate commit may be net-positive. 3B — add
only Settings-specific compositions (ActionBar, Swatches, Segmented, shared
scrolling), migrate Settings fully, and delete every newly orphaned
implementation and PageForm member. The combined phase-3 range from the
phase-2 acceptance must be net-negative; if Appearance plus Settings cannot
reach that, stop with the real diff and ownership ledger before expanding
scope. Animation and Pose surfaces stay in phase 4. Validate at 100%, 125%
and 150%.

### 4. Remaining leaf controls and surfaces

Migrate by real dependency: Settings; shell/sidebar/pickers; Animation; then
Pose inspector/rail/maps/matrix. Normalize each required control against the
catalog immediately before its first migrated surface. Specialized gizmos and
canvases retain their geometry while their surrounding chrome uses components.

### 5. Severance and audit

Delete the old static controls/compositions, ControlStyle/ControlSizing,
PageForm, public legacy Interactive/Motion facades and pane-local layout
helpers when their consumer inventory reaches zero. Grep for ordinary raw
ImGui widgets, manual cursor layout and duplicate paint. Replace the oversized
historical UI prose with one short durable architecture contract.

## Slice gates

1. The user-facing sheet remains exactly Picto | new Crystarium | red diff.
   Accepted `c71d682` candidate crops/hashes provide automated legacy A/B;
   no fourth legacy column or slow theme matrix is added.
2. Significant pixels, bounds, motion and interaction do not regress without
   an explicit user decision.
3. One arranged rectangle owns layout, clip, item registration, paint and
   help.
4. Product authoring meets the simplicity contract above and does not expose
   renderer mechanics.
5. Old paths gain no consumers; each bridge has a recorded final owner and is
   deleted when that owner migrates.
6. Exact gross additions, deletions and net handwritten production/tooling
   lines are reported.
7. Each completed vertical product-surface slice is net-negative; the whole
   PBI must be materially net-negative.
8. In-game acceptance is required for composed surfaces; compilation is not
   visual evidence.

## Completion criteria

- Stateless and typed-state components are intuitive to author and compose.
- Keys, typed state, refs, controlled values and unmount rules are deterministic.
- Style inheritance, variants, slots and `sx` have one typed resolution path.
- Flow, resize, overflow, scrolling and rounding have one owner.
- Input, focus, capture, motion, occlusion and portals have one owner.
- Runtime SVG is the only icon rendering path.
- Migrated panes contain no ordinary manual layout/input/paint recipes.
- The accepted UI remains visually and behaviorally equivalent in game.
- The whole rewrite is materially smaller: from about 23,756 handwritten
  production UI lines toward <=18k, with <=16k a stretch rather than a reason
  to hide ownership or rebuild magic.

## Non-goals

- A browser, DOM, retained layout tree or general virtual-DOM diff.
- React hooks/effects, Concurrent Mode, Suspense or public memoization.
- CSS parsing, selectors, class names, specificity or arbitrary HTML parity.
- CSS Grid, general wrapping, tables or generic z-index positioning.
- Generic subtree compositing or a second text/icon rasterizer.
- Replacing application/domain state with component state.
- Redesigning the accepted catalog during migration.
- Reviving Norvrandt/Stylesheet/FlexSolver under new names.
