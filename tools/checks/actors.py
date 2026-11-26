"""Actor copy and removal: copy the actor with its pose, remove the copy the way the
Remove button does, and undo and redo the removal."""
from bridge import Checks, actors, get, ready, short, wait_until

checks = Checks("actor copy and removal")
ready(checks)

BONE = "j_ude_a_l"  # Left arm in the bone list.


def rotation(actor):
    modification = get("/bone", actor=actor, name=BONE, partial=0).get("authoredModification")
    return modification and modification["rotation"]


get("/setbone", actor=0, name=BONE, partial=0, deg=40, axis="z")
posed = rotation(0)
checks.step("pose the left arm", posed is not None, short(posed))

copy = get("/dupepose", actor=0)
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=10)
if not checks.step("copy the actor with its pose", copy.get("ok") and ok, f"answer {short(copy, 160)}, {len(listed)} actors"):
    checks.finish()
matching, copied = wait_until(lambda: rotation(1),
                              lambda r: r is not None and abs(sum(r[k] * posed[k] for k in "XYZW")) > 0.9999, timeout=10)
checks.step("the copy has the same pose", matching, f"copy's left arm {copied}, actor's {posed}")

copy_id = listed[1]["id"]
get("/selection", entities=f"actor:{copy_id}")
get("/selectionremove", start=1)
ok, result = wait_until(lambda: get("/selectionremove"), lambda r: r.get("running") is False, timeout=20)
checks.step("remove the copy", ok and len(actors()) == 1, f"{len(actors())} actors, {short(result, 160)}")

undo = get("/undo")
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=15)
checks.step("undo brings the copy back", undo.get("ok") and ok, f"{len(listed)} actors")
matching, copied = wait_until(lambda: rotation(1),
                              lambda r: r is not None and abs(sum(r[k] * posed[k] for k in "XYZW")) > 0.9999, timeout=10)
checks.step("with its pose", matching, f"copy's left arm {copied}")

redo = get("/redo")
ok, listed = wait_until(actors, lambda a: len(a) == 1, timeout=15)
checks.step("redo removes it again", redo.get("ok") and ok, f"{len(listed)} actors")

checks.finish()
