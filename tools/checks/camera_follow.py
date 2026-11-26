"""Camera follow: the default camera follows a copy of the actor, the copy is removed,
and undo brings the copy back with the camera following it again."""
from bridge import Checks, actors, get, ready, short, wait_until

checks = Checks("camera follow")
ready(checks)

default = next(c for c in get("/cameras")["cameras"] if c["IsDefault"])
camera = f"camera:{default['id']}"


def followed():
    return get("/cameratarget", camera=camera).get("followedActor")


copy = get("/clone", actor=0)
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=10)
if not checks.step("copy the actor", copy.get("ok") and ok, f"answer {short(copy, 160)}, {len(listed)} actors"):
    checks.finish()
copy_id = copy["id"]

follow = get("/cameratarget", camera=camera, actor=f"actor:{copy_id}")
checks.step("the camera follows the copy", follow.get("applied") and followed() == copy_id,
            f"answer {short(follow, 200)}")

get("/selection", entities=f"actor:{copy_id}")
get("/selectionremove", start=1)
ok, _ = wait_until(lambda: get("/selectionremove"), lambda r: r.get("running") is False, timeout=20)
checks.step("remove the copy", ok and len(actors()) == 1, f"{len(actors())} actors, camera follows {followed()}")

undo = get("/undo")
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=15)
back = listed[1]["id"] if len(listed) == 2 else None
following, now = wait_until(followed, lambda f: f is not None and f == back, timeout=5)
checks.step("undo brings the copy back", undo.get("ok") and ok, f"{len(listed)} actors, the copy is {back}")
checks.step("and the camera follows it again", following, f"camera follows {now}")

checks.finish()
