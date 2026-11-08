# Shared placement (#667)

Candidate `b68c7040` uses the configured placement for new entities, duplicates,
MCDF actors and file loads. The Library's separate preference object and the
spawn browser's placement calculation were removed. The same-map scene default
remains As saved.

Release compilation and all 906 tests pass. Existing placement and posed-copy
tests were extended; no tests were added or removed. The six canonical deployed
assemblies matched the candidate.

Live checks covered actors, props, lights, colliders, orbit/free cameras, a minion,
a mount, an MCDF spawn/import, grouped copies, posed copies and creation undo/redo.
New entities were measured in front-of-camera, relative-to-camera and
relative-to-actor modes. Selecting a bone correctly uses its actor as the anchor.
Group copies retained their separation and landed their centre on the anchor.
Light, orbit-camera and collider files passed As saved and In front of camera;
relative light offsets and free-camera position/heading also passed.

The first actor check caught placement running before native spawning finished.
Placement now runs through the existing body-ready callback, and the repeat passed.
An orbit-camera reading differed during the first sequence of camera switches.
Two fresh repetitions and a repetition starting at the actor's position restored
the requested position within 0.00003 game units; that initial shift was not reproduced.

The original placement setting was restored and the main camera made active.
No errors or exceptions appeared in the recent log. Numerical checks are in
[the results file](shared-placement-2026-11-08.json). UI acceptance remains with Midona.

Comment/guard review: GPose and framework-thread checks protect native creation;
missing anchors and invalid entity bindings are reachable external conditions.
Screen overlays have no world position, so group placement excludes them from
the centre calculation. File loads now report unavailable anchors consistently.
The touched comments explain these rules and deferred actor placement; obsolete
historical placement prose was removed.

UI check: change Settings → Spawning → Placement, then add an actor and a light,
duplicate a group, and load a camera file. Compare In front of camera with Relative
to actor. The same choice should apply throughout, and group spacing should stay intact.
