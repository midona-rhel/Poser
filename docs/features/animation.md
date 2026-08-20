# Animation

Basic mode exposes one General Full Body authority. Its picker lists Base-compatible
emotes, actions, and raw timelines; Choose stages a value, Apply plays it, and Reset
restores the immutable incoming Base state. Play emote start selects the native
emote lifecycle for index-zero emotes. Loop is sticky Full Body intent.

Advanced mode restores Basic ownership before enabling independent Full Body,
Upper Body, Additive, and Lips selection and speed. Full Body and Upper Body also
provide scrub; Full Body alone provides Loop. The layer controls remain visible
and inert while Advanced is off. Returning to Basic restores every Advanced layer
before General commands become available.

Every picker filters by compatible native slot. General can further narrow the
catalog to Emotes, Actions, or Raw entries. Search matches display name, native
timeline id, and sheet key. Results show only name, the native layer they apply
to, and timeline id. Native route metadata remains internal.

Loop belongs only to Full Body. It can be armed before selection without
capturing or writing native state. Apply gives every explicit Full Body selection
the verified forced base timeline, including entries marked as native loops; the
sheet flag is not a substitute for the sequencer field. A non-Base write briefly
releases that global force, routes through its own native slot, then rearms the
same Full Body timeline; ordinary layer composition therefore keeps Base looping.
Catalog emotes use the game emote entry point for their intro and loop lifecycle;
raw timelines use the audited timeline setter. If the client clears Poser's
forced field when a Base animation ends, the framework update replays Selected
before restoring the field instead of treating a field rewrite as playback.

Advanced Facial and Pose > Expression are two views of one held-expression
authority. Both stage an expression and freeze Facial speed at zero when it is
applied. Pose calls that action Preview and also provides Bake. Apply drives only
the Facial slot for two validated framework ticks before freezing it, so a paused
actor can evaluate the chosen face without changing overall speed. Reset uses a
Straight Face bridge and immediately restores the first captured Facial timeline
and speed. Action-unit sliders remain a separate named pose layer.

Full Body and Upper Body expose scrub through their verified slot-index lookup,
searched across live skeleton partials and guarded by the captured skeleton
identity. Other layers omit scrub because numeric Havok indexes are not a stable
logical-layer mapping
(`Brio/Brio/UI/Controls/Editors/ActionTimelineEditor.cs:468-517`,
`Ktisis/Interface/Components/Chara/AnimationEditorTab.cs:267-307`). It also omits
global repeat and selection for Parts/Overlay slots. Lips is included because its
native override and logical-slot speed route are exact; Additive uses the same
sheet-routed selection and logical-slot speed contract as Upper and Facial
(`Brio/Brio/Capabilities/Actor/ActionTimelineCapability.cs:57-92`,
`Ktisis/Editor/Animation/AnimationManager.cs:88-112`).
