"""Camera: add a free camera, make it live, change its field of view, angle and lock
through the camera controls, undo a change, and remove the camera and undo that."""
from bridge import Checks, get, ready, short, wait_until

checks = Checks("camera")
ready(checks)


def cameras():
    return get("/cameras").get("cameras", [])


def camera(camera_id):
    return next((c for c in cameras() if c["id"] == camera_id), None)


before = {c["id"] for c in cameras()}
created = get("/camera", create="Free")
ok, after = wait_until(cameras, lambda cs: len({c["id"] for c in cs} - before) == 1, timeout=5)
new = [c for c in after if c["id"] not in before]
if not checks.step("add a free camera", ok, f"answer {short(created, 160)}, cameras {len(before)} -> {len(after)}"):
    checks.finish()
camera_id = new[0]["id"]

live = get("/camera", camera=camera_id, action="live")
checks.step("make it live", live.get("Success") and camera(camera_id)["IsLive"], short(live))

old_fov = camera(camera_id)["FoV"]
changed = get("/camera", camera=camera_id, action="fov", x=0.5)
ok, reading = wait_until(lambda: camera(camera_id), lambda c: abs(c["FoV"] - 0.5) < 0.001, timeout=3)
checks.step("set the field of view to 0.5", changed.get("Success") and ok, f"was {old_fov}, now {reading['FoV']}")
get("/undo")
ok, reading = wait_until(lambda: camera(camera_id), lambda c: abs(c["FoV"] - old_fov) < 0.001, timeout=3)
checks.step("undo puts the field of view back", ok, f"now {reading['FoV']}")

turned = get("/camera", camera=camera_id, action="angle", x=0.3, y=-0.2)
ok, reading = wait_until(lambda: camera(camera_id),
                         lambda c: abs(c["Angle"]["X"] - 0.3) < 0.001 and abs(c["Angle"]["Y"] + 0.2) < 0.001, timeout=3)
checks.step("set the angle", turned.get("Success") and ok, short(reading["Angle"]))

locked = get("/camera", camera=camera_id, action="lock", value=1)
checks.step("lock the camera", locked.get("Success") and camera(camera_id)["IsLocked"], short(locked))
get("/camera", camera=camera_id, action="lock", value=0)

get("/selection", entities=f"camera:{camera_id}")
get("/selectionremove", start=1)
ok, _ = wait_until(lambda: get("/selectionremove"), lambda r: r.get("running") is False, timeout=10)
checks.step("remove the camera", ok and camera(camera_id) is None, f"{len(cameras())} cameras left")

get("/undo")
ok, back = wait_until(cameras, lambda cs: len(cs) == len(after), timeout=5)
checks.step("undo brings a camera back", ok, f"{len(back)} cameras")

checks.finish()
