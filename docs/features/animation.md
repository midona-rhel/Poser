# Animation

Poser exposes Full Body, Upper Body, Facial, Additive, and Lips as independent
layers. Each layer shows the current native timeline and a session-only Selected
timeline. Choosing Selected plays immediately. Reset restores the state captured
before Poser's first write, and clears Selected only after restoration succeeds.

Each layer has an exact logical-slot speed override. Pause writes zero and
remembers the previous nonzero native or user speed; Play restores that speed.
Selected remains after a one-shot stops. Poser does not infer a completion event:
when Current differs from Selected, Play replays Selected through the same native
selection route.

Repeat belongs only to Full Body. It can be armed before selection without
capturing or writing native state. A selected animation that loops natively needs
no forced timeline. For a non-looping selection, Poser owns the verified forced
base field transactionally. Selecting another layer suspends that force while
preserving repeat intent, so the two controls do not fight.

Animation > Facial and Pose > Expression share one Facial selection and restore
point. Preview pauses that shared layer. A later Facial selection resumes the
remembered speed and becomes the single authority. Expression action-unit sliders
remain a separate pose layer and can compose with facial animation.

Poser intentionally omits raw Havok scrubbing: the indexes shown by Brio and
Ktisis are useful diagnostics but are not a stable logical-layer mapping
(`Brio/Brio/UI/Controls/Editors/ActionTimelineEditor.cs:468-517`,
`Ktisis/Interface/Components/Chara/AnimationEditorTab.cs:267-307`). It also omits
global repeat and selection for Parts/Overlay slots. Lips is included because its
native override and logical-slot speed route are exact; Additive uses the same
sheet-routed selection and logical-slot speed contract as Upper and Facial
(`Brio/Brio/Capabilities/Actor/ActionTimelineCapability.cs:57-92`,
`Ktisis/Editor/Animation/AnimationManager.cs:88-112`).
