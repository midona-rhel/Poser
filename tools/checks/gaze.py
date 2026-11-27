"""Gaze: look at the camera, look at a point and undo it, look at a copy of the actor,
and see the gaze go stale when the copy is removed."""
from bridge import Checks, copy_actor, get, ready, remove, short, wait_until

checks = Checks("gaze")
ready(checks)


def gaze():
    return get("/gaze", actor=0)


mode = get("/gazemode", actor=0, mode="Camera")
checks.step("look at the camera", mode.get("ok") and gaze().get("mode") == "Camera", short(gaze(), 200))

point = get("/gazepoint", actor=0, x=1.5, y=1.6, z=2.0)
reading = gaze()
checks.step("look at a point", point.get("ok") and reading.get("mode") == "Position"
            and abs((reading.get("anchor") or {}).get("x", 0) - 1.5) < 0.01, short(reading, 200))
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
