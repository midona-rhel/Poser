"""Camera follow: the default camera follows a copy of the actor, the copy is removed,
and undo brings the copy back with the camera following it again."""
from bridge import Checks, actors, copy_actor, get, ready, remove, short, wait_until

checks = Checks("camera follow")
ready(checks)

default = next(c for c in get("/cameras")["cameras"] if c["IsDefault"])
camera = f"camera:{default['id']}"


def followed():
    return get("/cameratarget", camera=camera).get("followedActor")


copy_id = copy_actor(checks)
if copy_id is None:
    checks.finish()

follow = get("/cameratarget", camera=camera, actor=f"actor:{copy_id}")
checks.step("the camera follows the copy", follow.get("applied") and followed() == copy_id,
            f"answer {short(follow, 200)}")

removed, saw = remove([f"actor:{copy_id}"])
checks.step("remove the copy", removed and len(actors()) == 1, f"{len(actors())} actors, camera follows {followed()}, {saw}")

undo = get("/undo")
ok, listed = wait_until(actors, lambda a: len(a) == 2, timeout=15)
back = listed[1]["id"] if len(listed) == 2 else None
following, now = wait_until(followed, lambda f: f is not None and f == back, timeout=5)
checks.step("undo brings the copy back", undo.get("ok") and ok, f"{len(listed)} actors, the copy is {back}")
checks.step("and the camera follows it again", following, f"camera follows {now}")

checks.finish()
