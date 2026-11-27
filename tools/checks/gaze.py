"""Gaze: look at the camera, look at a point, move the point and undo both, look at a
copy of the actor, and see the gaze go stale when the copy is removed."""
from bridge import Checks, copy_actor, get, ready, remove, short, wait_until

checks = Checks("gaze")
ready(checks)


def gaze():
    return get("/gaze", actor=0)


mode = get("/gazemode", actor=0, mode="Camera")
checks.step("look at the camera", mode.get("ok") and gaze().get("mode") == "Camera", short(gaze(), 200))

# The point can be moved only once the gaze looks at a point, as in the Gaze section.
mode = get("/gazemode", actor=0, mode="Position")
checks.step("look at a point", mode.get("ok") and gaze().get("mode") == "Position", short(mode, 120))


def anchor_x(reading):
    return (reading.get("anchor") or {}).get("x")


point = get("/gazepoint", actor=0, x=1.5, y=1.6, z=2.0)
ok, reading = wait_until(gaze, lambda g: anchor_x(g) is not None and abs(anchor_x(g) - 1.5) < 0.01, timeout=3)
checks.step("move the point", point.get("ok") and ok, f"{short(point, 120)}, anchor {reading.get('anchor')}")
get("/undo")
ok, reading = wait_until(gaze, lambda g: g.get("mode") == "Position" and anchor_x(g) is not None
                         and abs(anchor_x(g) - 1.5) > 0.01, timeout=3)
checks.step("undo moves the point back", ok, f"mode {reading.get('mode')}, anchor {reading.get('anchor')}")
get("/undo")
ok, reading = wait_until(gaze, lambda g: g.get("mode") == "Camera", timeout=3)
checks.step("undo goes back to the camera", ok, f"mode {reading.get('mode')}")

copy_id = copy_actor(checks)
if copy_id is not None:
    target = get("/gazetarget", actor=0, target=f"actor:{copy_id}")
    checks.step("look at the copy", target.get("applied") and target.get("target") == copy_id
                and target.get("mode") == "Entity", short(target, 200))
    removed, saw = remove([f"actor:{copy_id}"])
    ok, reading = wait_until(gaze, lambda g: g.get("stale") is True, timeout=5)
    checks.step("removing the copy leaves the gaze target stale", removed and ok, f"{short(reading, 200)}, {saw}")

off = get("/gazemode", actor=0, mode="None")
checks.step("stop the gaze", off.get("ok") and gaze().get("mode") == "None", short(gaze(), 120))

checks.finish()
