# TransformTargetResolver

## Purpose

`TransformTargetResolver` is the one shared answer to "what does a transform
act on". It derives an **effective transform selection** from the ordered
`SelectionSession` list and the current `SceneSnapshot`, and both transform
surfaces — the inspector and the gizmo — consume the identical resolution for
displayed values, gesture baselines, ordered target lists, and placement.

## Resolution rules

1. An empty or unresolvable selection yields no result; no gesture begins.
2. Actor selections resolve in original selection order; the first actor is
   the effective primary.
3. **Every selected bone is a target.** The user explicitly reversed
   PBI-001's descendant filtering in the 2026-07-24 walkthrough: selecting a
   knee and its calf must transform both (the classic same-chain pair). This
   cannot compound into a feedback loop because the gesture service applies
   each target absolutely from its own frozen Begin baseline — an ancestor's
   propagation is overridden by the descendant's own absolute write within
   the same update.
4. The **first selected bone** is the effective transform primary.
5. `Targets` preserves original selection order with the primary first.

## Selection primary vs effective transform primary

`SelectionSession.Primary` remains the selection primary used for selection
display (rail header, tree highlight). The effective transform primary may
differ when the selection primary is a descendant of another selected bone;
the PBI-001 clarification section records this distinction.

## Ownership

The resolver is a pure application query: no state, no native access, no
retained output. Callers re-resolve per frame or per gesture Begin; the
gesture service still freezes its own baselines at Begin through the runtime
port capture.
