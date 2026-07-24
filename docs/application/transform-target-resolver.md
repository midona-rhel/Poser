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
3. Bone selections drop every selected bone whose ancestor chain (snapshot
   `BoneDescriptor.Parent` links) contains another selected bone — the
   ancestor's propagation already carries the edit, so parent/child
   selections never compound.
4. The **first surviving root in original selection order** is the effective
   transform primary. The resolver never selects a globally shallowest
   unrelated bone and never re-adds a filtered descendant — including a
   filtered selection primary.
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
