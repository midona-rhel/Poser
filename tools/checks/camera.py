"""Camera: add a free camera, make it live, change its field of view, angle and lock
through the camera controls, undo a change, and remove the camera and undo that."""
from bridge import Checks, get, ready, remove, short, wait_until

checks = Checks("camera")
ready(checks)


def cameras():
    return get("/cameras").get("cameras", [])


def camera(camera_id):
    """The camera's values, or an empty dictionary when it is gone."""
    return next((c for c in cameras() if c["id"] == camera_id), {})


def near(value, wanted):
    return value is not None and abs(value - wanted) < 0.001


before = {c["id"] for c in cameras()}
created = get("/camera", create="Free")
ok, after = wait_until(cameras, lambda cs: len({c["id"] for c in cs} - before) == 1, timeout=5)
new = [c for c in after if c["id"] not in before]
if not checks.step("add a free camera", ok, f"answer {short(created, 160)}, cameras {len(before)} -> {len(after)}"):
    checks.finish()
camera_id = new[0]["id"]

live = get("/camera", camera=camera_id, action="live")
checks.step("make it live", live.get("Success") and camera(camera_id).get("IsLive"), short(live))

old_fov = camera(camera_id).get("FoV")
changed = get("/camera", camera=camera_id, action="fov", x=0.5)
ok, reading = wait_until(lambda: camera(camera_id), lambda c: near(c.get("FoV"), 0.5), timeout=3)
checks.step("set the field of view to 0.5", changed.get("Success") and ok, f"was {old_fov}, now {reading.get('FoV')}")
get("/undo")
ok, reading = wait_until(lambda: camera(camera_id), lambda c: near(c.get("FoV"), old_fov), timeout=3)
checks.step("undo puts the field of view back", ok, f"now {reading.get('FoV')}")

turned = get("/camera", camera=camera_id, action="angle", x=0.3, y=-0.2)
ok, reading = wait_until(lambda: camera(camera_id),
                         lambda c: near((c.get("Angle") or {}).get("X"), 0.3) and near((c.get("Angle") or {}).get("Y"), -0.2),
                         timeout=3)
checks.step("set the angle", turned.get("Success") and ok, short(reading.get("Angle")))

locked = get("/camera", camera=camera_id, action="lock", value=1)
checks.step("lock the camera", locked.get("Success") and camera(camera_id).get("IsLocked") is True, short(locked))
get("/camera", camera=camera_id, action="lock", value=0)

removed, saw = remove([f"camera:{camera_id}"])
checks.step("remove the camera", removed and not camera(camera_id), f"{len(cameras())} cameras left, {saw}")

get("/undo")
ok, back = wait_until(cameras, lambda cs: len(cs) == len(after), timeout=5)
checks.step("undo brings a camera back", ok, f"{len(back)} cameras")

checks.finish()
