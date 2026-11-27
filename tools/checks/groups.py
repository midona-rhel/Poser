"""Groups and parenting: group and ungroup lights, attach one light to another and undo
it, and check what a child does when its parent moves: it follows the parent's rotation
but not its scale, nested parents move together, a link that would make a loop is
refused, and undoing the removal of a parent makes the child follow it again."""
import math

from bridge import Checks, add_light, get, parenting_list, ready, remove, short, wait_until

checks = Checks("groups and parenting")
ready(checks)


def entity(selection_id):
    return next((e for e in parenting_list() if e["id"] == selection_id), None)


def world(selection_id):
    return (entity(selection_id) or {}).get("world") or {}


def position(selection_id):
    return world(selection_id).get("Position")


def parent(selection_id):
    return (entity(selection_id) or {}).get("parent")


def link(child, target):
    return get("/parenting", child=child, parent=target or "none")


def move(selection_id, **change):
    return get("/parenting", child=selection_id, **change)


def offset(a, b):
    return [b[k] - a[k] for k in "XYZ"]


def near(a, b, tolerance=0.01):
    return a is not None and b is not None and all(abs(x - y) < tolerance for x, y in zip(a, b))


def xyz(p):
    return p and [round(p[k], 3) for k in "XYZ"]


a, b, c = add_light(), add_light(), add_light()
if not checks.step("add three lights", a and b and c, f"{a}, {b}, {c}"):
    checks.finish()

# Groups.
group = get("/group", action="create", name="Check group", members=f"{a},{b}")
listed = get("/groups").get("groups", [])
found = next((g for g in listed if g["groupId"] == group.get("groupId")), None)
checks.step("group two lights", group.get("success") and found and set(found["members"]) == {a, b},
            short(found or group))
ungroup = get("/group", action="remove", group=group.get("groupId"))
checks.step("ungroup them", ungroup.get("success"), short(ungroup))

# Attach, follow a move, and undo both.
attach = link(b, a)
checks.step("attach the second light to the first", parent(b) == a, short(attach, 160))
child_before = position(b)
moved = move(a, dx=0.5)
ok, child_after = wait_until(lambda: position(b),
                             lambda p: p is not None and child_before is not None and abs(p["X"] - child_before["X"] - 0.5) < 0.01, timeout=3)
checks.step("move the parent 0.5 to the side and the child follows", ok,
            f"child X {child_before and child_before['X']} -> {child_after and child_after['X']}, {short(moved, 120)}")
get("/undo")
get("/undo")
ok, _ = wait_until(lambda: parent(b), lambda p: p is None, timeout=3)
checks.step("undo the move and the attachment", ok, f"parent {parent(b)}")

# 1. The child follows the parent's rotation, not its scale.
move(b, dx=2)
start_a, start_b = position(a), position(b)
scale_b = world(b).get("Scale")
link(b, a)
turned = move(a, yaw=90, scale=3)
# Turning +90 degrees about the up axis takes (x, y, z) to (z, y, -x).
dx, dy, dz = offset(start_a, start_b)
expected = [start_a["X"] + dz, start_a["Y"] + dy, start_a["Z"] - dx]
ok, now_b = wait_until(lambda: position(b), lambda p: near(xyz(p), expected), timeout=3)
distance = now_b and math.dist(xyz(position(a)), xyz(now_b))
checks.step("turn the parent 90 degrees and the child swings round it", ok,
            f"child {xyz(start_b)} -> {xyz(now_b)}, expected {[round(v, 3) for v in expected]}, {short(turned, 120)}")
checks.step("the child stays 2 units away while the parent has scale 3",
            distance is not None and abs(distance - 2) < 0.01 and near(xyz(world(a).get("Scale")), [3, 3, 3]),
            f"distance {distance}, parent scale {xyz(world(a).get('Scale'))}")
checks.step("the child's own scale does not change", near(xyz(world(b).get("Scale")), xyz(scale_b)),
            f"{xyz(scale_b)} -> {xyz(world(b).get('Scale'))}")
link(b, None)
before = position(b)
move(a, dx=1)
checks.step("after unlinking, moving the parent leaves the child", parent(b) is None and near(xyz(position(b)), xyz(before)),
            f"parent {parent(b)}, child {xyz(before)} -> {xyz(position(b))}")

# 2. Nested parents: C follows B, B follows A, and all move together.
move(c, dz=1)
link(b, a)
link(c, b)
before_b, before_c = position(b), position(c)
move(a, dx=10)
reading = parenting_list()  # One reading, so B and C are read from the same frame.
now_b = next((e["world"]["Position"] for e in reading if e["id"] == b), None)
now_c = next((e["world"]["Position"] for e in reading if e["id"] == c), None)
shift_b = now_b and offset(before_b, now_b)
shift_c = now_c and offset(before_c, now_c)
checks.step("move the top parent 10 and both children move 10 in the same frame",
            parent(c) == b and near(shift_b, [10, 0, 0]) and near(shift_c, [10, 0, 0]),
            f"B moved {shift_b and [round(v, 3) for v in shift_b]}, C moved {shift_c and [round(v, 3) for v in shift_c]}")
gap_before = offset(before_b, before_c)
gap_after = now_b and now_c and offset(now_b, now_c)
checks.step("C keeps its offset from B", near(gap_after, gap_before),
            f"{[round(v, 3) for v in gap_before]} -> {gap_after and [round(v, 3) for v in gap_after]}")

# 3. A link that would make a loop is refused.
positions = [xyz(position(x)) for x in (a, b, c)]
loop = link(a, c)
checks.step("attaching A to C, which follows A, is refused",
            loop.get("Success") is False and loop.get("Detail") == "Parenting would create a cycle."
            and parent(a) is None and [xyz(position(x)) for x in (a, b, c)] == positions,
            f"answer {short(loop, 120)}, parent of A {parent(a)}")
everything = parenting_list(bones=True)
actor = next((e["id"] for e in everything if e["id"].startswith("actor:")), None)
bone = next((e["id"] for e in everything if e["id"].startswith("bone:")), None)
own_bone = link(actor, bone) if actor and bone else {}
checks.step("attaching an actor to one of its own bones is refused",
            own_bone.get("Success") is False and own_bone.get("Detail") == "Parenting would create a cycle."
            and parent(actor) is None,
            f"answer {short(own_bone, 120)}")

# 4. Undoing the removal of a parent brings it back, and the child follows it again.
link(c, None)
removed, saw = remove([a])
checks.step("remove the parent light", removed and entity(a) is None, saw)
undo = get("/undo")
ok, restored = wait_until(lambda: parent(b), lambda p: p is not None and p != a and entity(p) is not None, timeout=10)
checks.step("undo brings the parent back with a new id and the child's parent is that light",
            undo.get("ok") and ok, f"parent of B {restored}")
if ok:
    before = position(b)
    move(restored, dx=1)
    ok, after = wait_until(lambda: position(b), lambda p: near(offset(before, p), [1, 0, 0]), timeout=3)
    checks.step("moving the restored parent moves the child", ok, f"child {xyz(before)} -> {xyz(after)}")
    a = restored

for light in (c, b, a):
    link(light, None)
    get("/parenting", child=light, remove=1)
checks.step("remove the lights", all(entity(x) is None for x in (a, b, c)), "")

checks.finish()
