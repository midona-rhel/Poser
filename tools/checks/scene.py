"""Scene save and load: pose the actor, save the scene to a new file, reset the actor,
load the scene back onto the same actor, and undo the load."""
import os
import tempfile

from bridge import Checks, actors, get, ready, short, wait_until

checks = Checks("scene save and load")
ready(checks)

BONE = "j_ude_a_l"  # Left arm in the bone list.


def rotation():
    modification = get("/bone", actor=0, name=BONE, partial=0).get("authoredModification")
    return modification and modification["rotation"]


def same(a, b):
    return a is not None and b is not None and abs(sum(a[k] * b[k] for k in "XYZW")) > 0.9999


def scene_idle():
    return get("/scene")


posed = get("/setbone", actor=0, name=BONE, partial=0, deg=40, axis="z")
saved_rotation = rotation()
checks.step("pose the left arm", posed.get("ok") and saved_rotation is not None, short(posed, 160))

path = os.path.join(tempfile.mkdtemp(prefix="poser-check-"), "check.xivs")
save = get("/scene", path=path, save=1)
ok, reading = wait_until(scene_idle, lambda s: s.get("busy") is False, timeout=20)
checks.step("save the scene to a new file", save.get("ok") and ok and os.path.getsize(path) > 0,
            f"answer {short(save, 160)}, file {os.path.getsize(path) if os.path.exists(path) else 'missing'} bytes")

get("/resetall", actor=0)
ok, _ = wait_until(rotation, lambda r: r is None, timeout=10)
checks.step("Reset → All clears the pose", ok, f"left arm edit {rotation()}")

load = get("/scene", path=path, clear=0, placement="AsSaved")
ok, reading = wait_until(scene_idle, lambda s: s.get("busy") is False, timeout=30)
loaded, now = wait_until(rotation, lambda r: same(r, saved_rotation), timeout=10)
checks.step("load the scene onto the same actor", load.get("ok") and ok and loaded and len(actors()) == 1,
            f"answer {short(load, 160)}, {len(actors())} actors, left arm {now}, saved {saved_rotation}")

undo = get("/undo")
ok, now = wait_until(rotation, lambda r: r is None, timeout=15)
checks.step("undo the load", undo.get("ok") and ok, f"undid {undo.get('history', {}).get('redo')}, left arm edit {now}")

checks.finish()
