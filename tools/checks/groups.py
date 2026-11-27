"""Groups and parenting: add two lights, group them and ungroup them, attach one light
to the other, move the parent and see the child follow, and undo the attachment."""
from bridge import Checks, get, ready, short, wait_until

checks = Checks("groups and parenting")
ready(checks)


def entity(selection_id):
    listed = get("/parenting")
    return next((e for e in listed if e["id"] == selection_id), None) if isinstance(listed, list) else None


def position(selection_id):
    world = (entity(selection_id) or {}).get("world") or {}
    return world.get("Position")


first = get("/parenting", create="light").get("id")
second = get("/parenting", create="light").get("id")
if not checks.step("add two lights", first and second, f"{first}, {second}"):
    checks.finish()

group = get("/group", action="create", name="Check group", members=f"{first},{second}")
listed = get("/groups").get("groups", [])
found = next((g for g in listed if g["groupId"] == group.get("groupId")), None)
checks.step("group them", group.get("success") and found and set(found["members"]) == {first, second},
            short(found or group))
ungroup = get("/group", action="remove", group=group.get("groupId"))
checks.step("ungroup them", ungroup.get("success"), short(ungroup))

attach = get("/parenting", child=second, parent=first)
checks.step("attach the second light to the first", (entity(second) or {}).get("parent") == first, short(attach, 160))

child_before = position(second)
moved = get("/parenting", child=first, dx=0.5)
ok, child_after = wait_until(lambda: position(second),
                             lambda p: p is not None and child_before is not None and abs(p["X"] - child_before["X"] - 0.5) < 0.01, timeout=3)
checks.step("move the parent 0.5 to the side and the child follows", ok,
            f"child X {child_before and child_before['X']} -> {child_after and child_after['X']}, {short(moved, 120)}")

get("/undo")
get("/undo")
ok, _ = wait_until(lambda: (entity(second) or {}).get("parent"), lambda p: p is None, timeout=3)
checks.step("undo the move and the attachment", ok, f"parent {(entity(second) or {}).get('parent')}")

for light in (first, second):
    get("/parenting", child=light, remove=1)
checks.step("remove both lights", entity(first) is None and entity(second) is None, "")

checks.finish()
