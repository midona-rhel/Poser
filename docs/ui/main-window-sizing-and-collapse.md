# Main-window sizing and collapse

## Purpose

The main shell preserves the editor width when an inspector appears or
disappears. Its collapsed state is a real titlebar-only window, not an expanded
window whose content happens to be hidden.

## Width contract

`MainWindow` defines separate total-window minimums:

| State | Minimum logical width |
|---|---:|
| Tab without an inspector | 830 px |
| Pose, or another tab with an inspector | 1110 px |

The difference is exactly the 280 px inspector width. The initial Pose width is
1160 px: 50 px above its minimum. This 50 px increase belongs to the protected
main-workspace width, not the inspector, so it remains present whether the
inspector is visible or hidden.

When a retained route changes whether an inspector is present, `PreDraw` adds
or removes exactly 280 logical px from the current window width. This preserves
the editor/sidebar width instead of consuming it, and makes the transition
reversible. The current focused product exposes only Pose, which has an
inspector; the no-inspector value remains the shell invariant for a later route
that explicitly earns product scope.

The sidebar remains independently resizable between 220 and 400 px. That
changes the split inside the protected base width; it does not change the
inspector's additive rule.

## Collapse contract

Collapse stores the expanded logical height and requests exactly the 48 px
titlebar height. While collapsed:

- minimum and maximum height are both 48 px;
- the current width is retained, subject to the active tab's logical minimum;
- no sidebar, editor, inspector, toolbar, or statusbar is drawn;
- the titlebar is one continuous glass surface with no sidebar divider or
  inspector cell;
- expanding restores the saved height and the active tab's width constraint.

Size changes go through Dalamud's `Window.Size`/`SizeCondition` path in
`PreDraw`. Calling `ImGui.SetWindowSize` from the shell draw pass loses against
Dalamud's window management and must not be used for this behavior.

## Reference rationale

Brio separates dock/selection furniture from the posing surface, while Ktisis
keeps the transform workspace usable at compact sizes. Poser preserves the
workspace across navigation by treating its inspector as a reversible width
addition. Picto and DisplayFrame remain the references for the continuous
collapsed glass bar.

## Verification

1. Open Pose and shrink horizontally; the window stops at 1110 logical px.
2. Collapse Pose; the window becomes exactly one 48 px bar with no
   vertical sidebar seam or empty body.
3. Expand; the previous height returns and the 1110 px minimum applies.
4. Test at multiple Dalamud UI scales; all values above are logical pixels and
   must scale exactly once.

When a future retained no-inspector route is added, its acceptance must also
cover the reversible 280 px transition and 830 px minimum before that route is
considered complete.
