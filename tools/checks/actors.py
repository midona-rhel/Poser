"""Actor copy and removal: copy the actor with its pose, remove the copy the way the
Remove button does, and undo and redo the removal."""
from bridge import Checks, actors, copy_actor, get, idle, ready, remove, short, wait_until

checks = Checks("actor copy and removal")
ready(checks)

BONE = "j_ude_a_l"  # Left arm in the bone list.


def rotation(actor):
    modification = get("/bone", actor=actor, name=BONE, partial=0).get("authoredModification")
    return modification and modification["rotation"]


def same_as(posed):
    return lambda r: r is not None and abs(sum(r[k] * posed[k] for k in "XYZW")) > 0.9999


turned = get("/setbone", actor=0, name=BONE, partial=0, deg=40, axis="z")
# The edit shows up in the bone reading a frame after /setbone answers.
_, posed = wait_until(lambda: rotation(0), lambda r: r is not None, timeout=3)
if not checks.step("pose the left arm", turned.get("ok") and posed is not None, f"{short(turned, 120)}, left arm {short(posed)}"):
    checks.finish()

copy_id = copy_actor(checks, with_pose=True)
if copy_id is None:
    checks.finish()
matching, copied = wait_until(lambda: rotation(1), same_as(posed), timeout=10)
checks.step("the copy has the same pose", matching, f"copy's left arm {copied}, actor's {posed}")

removed, saw = remove([f"actor:{copy_id}"])
checks.step("remove the copy", removed and len(actors()) == 1, f"{len(actors())} actors, {saw}")

undo = get("/undo")
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=15)
checks.step("undo brings the copy back", undo.get("ok") and ok, f"{len(listed)} actors")
matching, copied = wait_until(lambda: rotation(1), same_as(posed), timeout=10)
checks.step("with its pose", matching, f"copy's left arm {copied}")

# Bringing the copy back runs as an action, and redo waits for it like any other.
idle()
redo = get("/redo")
ok, listed = wait_until(actors, lambda a: len(a) == 1, timeout=15)
checks.step("redo removes it again", redo.get("ok") and ok, f"{len(listed)} actors, redo {short(redo, 120)}")

checks.finish()
