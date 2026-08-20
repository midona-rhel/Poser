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
base field transactionally and reasserts it if a native animation update clears
it. Selecting another layer releases that force before playback and preserves
repeat intent, so the two controls do not fight.

Animation > Facial and Pose > Expression share one Facial selection and restore
point. Preview lets the timeline advance; Bake replays it before the delayed face
capture, so expressions whose first frame is neutral remain visible. A later
Facial selection becomes the single authority. Expression action-unit sliders
remain a separate pose layer and can compose with facial animation. The delayed
capture matches Ktisis's two-tick face synchronization after timeline playback
(`Ktisis/Editor/Animation/Handlers/AnimationEditor.cs:126-129`,
`Ktisis/Editor/Posing/PosingManager.cs:131-145`).

Poser intentionally omits raw Havok scrubbing: the indexes shown by Brio and
Ktisis are useful diagnostics but are not a stable logical-layer mapping
(`Brio/Brio/UI/Controls/Editors/ActionTimelineEditor.cs:468-517`,
`Ktisis/Interface/Components/Chara/AnimationEditorTab.cs:267-307`). It also omits
global repeat and selection for Parts/Overlay slots. Lips is included because its
native override and logical-slot speed route are exact; Additive uses the same
sheet-routed selection and logical-slot speed contract as Upper and Facial
(`Brio/Brio/Capabilities/Actor/ActionTimelineCapability.cs:57-92`,
`Ktisis/Editor/Animation/AnimationManager.cs:88-112`).
