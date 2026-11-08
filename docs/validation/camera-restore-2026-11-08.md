# Camera restoration (#689)

The original single-main-camera report did not reproduce on the starting build.
Its saved position returned within 0.001 game units and the new camera became active.

With two saved views, an inactive camera reported the current view's position
after loading. Saving again then overwrote that camera's saved position. The
camera already held the correct pending position; WorldPosition now uses it
until the camera first becomes active. No extra state or fallback was needed.

Loading cameras while excluding their actors also skipped the saved active
camera. Selection now happens before restoring the optional follow target.
The missing-target warning remains useful because the camera cannot follow
an actor the user excluded from the load.

Candidate `6e074df7` passed Release compilation and all 906 existing tests.
Two relevant tests gained assertions; no tests were added or removed.
All six deployed Poser assemblies matched the build output.

Live bridge checks passed for two-camera loading, saving and loading again,
restoring the active orbit and free cameras, and zooming after restoration.
A real timed whole-scene autosave also restored the active free camera's
position and roll. Original autosave settings were restored afterward.
Readbacks are in [the evidence file](camera-restore-2026-11-08.json).
The cameras-without-actors case was checked through the application workflow test.

The review covered camera capture, position readback and activation during scene
load. The existing native-availability, camera-kind, pin and missing-target
branches correspond to actual camera states. The misleading target comment was
rewritten and its repeated dictionary lookup removed.

User check: save two different camera views with the second active, load the
scene, save again, and load again. Both views should keep their positions; the
second should be active. Zoom should still work. User acceptance is pending.
