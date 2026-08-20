# Animation

Poser exposes Full Body, Upper Body, Facial, Additive, and Lips as independent
layers. Each layer shows the current native timeline and a session-only Selected
timeline. Choosing remembers Selected without playback; Apply plays it and keeps
the choice available for replay. The first Choose captures the immutable native
restore point for that actor and slot. Reset restores that exact incoming state,
releases the slot's overrides, and clears Selected only after restoration succeeds.

Each layer has an exact logical-slot speed override. Pause writes zero and
remembers the previous nonzero native or user speed; Play restores that speed.
Selected remains after a one-shot stops. Poser does not infer a completion event;
Apply and Play replay Selected through the same native selection route.

Repeat belongs only to Full Body. It can be armed before selection without
capturing or writing native state. Apply gives every explicit Full Body selection
the verified forced base timeline, including entries marked as native loops; the
sheet flag is not a substitute for the sequencer field. Selecting another layer
releases that force before playback and preserves repeat intent. Applying Full
Body later reclaims it, so the most recent explicit layer action is authoritative.

Animation > Facial and Pose > Expression share one Facial selection and restore
point. Preview lets the timeline advance; Bake replays it before the delayed face
capture, so expressions whose first frame is neutral remain visible. A later
Facial selection becomes the single authority. Expression action-unit sliders
remain a separate pose layer and can compose with facial animation. The delayed
capture matches Ktisis's two-tick face synchronization after timeline playback
(`Ktisis/Editor/Animation/Handlers/AnimationEditor.cs:126-129`,
`Ktisis/Editor/Posing/PosingManager.cs:131-145`).

Full Body alone exposes scrub through the control-zero lookup used by Brio and
Ktisis, searched across live skeleton partials and guarded by the captured
skeleton identity. Other layers omit scrub because numeric Havok indexes are not
a stable logical-layer mapping
(`Brio/Brio/UI/Controls/Editors/ActionTimelineEditor.cs:468-517`,
`Ktisis/Interface/Components/Chara/AnimationEditorTab.cs:267-307`). It also omits
global repeat and selection for Parts/Overlay slots. Lips is included because its
native override and logical-slot speed route are exact; Additive uses the same
sheet-routed selection and logical-slot speed contract as Upper and Facial
(`Brio/Brio/Capabilities/Actor/ActionTimelineCapability.cs:57-92`,
`Ktisis/Editor/Animation/AnimationManager.cs:88-112`).
