"""Posing and IK: turn a bone and undo and redo it, mirror the pose, reset the body,
switch IK on for a hand, and Reset → All with undo."""
from bridge import Checks, get, ready, short, wait_until

checks = Checks("posing and IK")
ready(checks)

LEFT_ARM, RIGHT_ARM, LEFT_HAND = "j_ude_a_l", "j_ude_a_r", "j_te_l"


def edited(bone):
    return get("/bone", actor=0, name=bone, partial=0).get("authoredModification") is not None


def ik():
    return get("/ik", actor=0, name=LEFT_HAND).get("config", {})


turned = get("/setbone", actor=0, name=LEFT_ARM, partial=0, deg=40, axis="z")
checks.step("turn the left arm", turned.get("ok") and edited(LEFT_ARM), short(turned, 200))
get("/undo")
ok, _ = wait_until(lambda: edited(LEFT_ARM), lambda e: not e, timeout=3)
checks.step("undo the turn", ok, f"left arm edited {edited(LEFT_ARM)}")
get("/redo")
ok, _ = wait_until(lambda: edited(LEFT_ARM), lambda e: e, timeout=3)
checks.step("redo the turn", ok, f"left arm edited {edited(LEFT_ARM)}")

mirror = get("/poseedit", actor=0, action="mirror")
ok, _ = wait_until(lambda: edited(RIGHT_ARM), lambda e: e, timeout=3)
checks.step("mirror puts the turn on the right arm", mirror.get("ok") and ok,
            f"answer {short(mirror, 160)}, right arm edited {edited(RIGHT_ARM)}")
get("/undo")

reset = get("/poseedit", actor=0, action="reset", region="Body")
ok, _ = wait_until(lambda: edited(LEFT_ARM), lambda e: not e, timeout=3)
checks.step("reset the body", reset.get("ok") and ok, f"answer {short(reset, 160)}")
get("/undo")
ok, _ = wait_until(lambda: edited(LEFT_ARM), lambda e: e, timeout=3)
checks.step("undo the body reset", ok, f"left arm edited {edited(LEFT_ARM)}")

step_before = get("/history").get("undo")
read = get("/ik", actor=0, name=LEFT_HAND)
checks.step("reading IK changes nothing", read.get("config", {}).get("Enabled") is False
            and get("/history").get("undo") == step_before,
            f"IK {read.get('config', {}).get('Enabled')}, undo step {get('/history').get('undo')}")

switched = get("/ik", actor=0, name=LEFT_HAND, action="set", enabled=1, solver="TwoJoint")
checks.step("switch IK on for the left hand", switched.get("ok") and ik().get("Enabled") is True,
            f"answer {switched.get('ok')} {switched.get('Detail')}, undo step {switched.get('history', {}).get('undo')}")

everything = get("/resetall", actor=0)
ok, _ = wait_until(lambda: (edited(LEFT_ARM), ik().get("Enabled")), lambda r: r == (False, False), timeout=10)
checks.step("Reset → All clears the pose and IK", everything.get("ok") and ok,
            f"answer {short(everything, 160)}, left arm edited {edited(LEFT_ARM)}, IK {ik().get('Enabled')}")
undo = get("/undo")
ok, _ = wait_until(lambda: (edited(LEFT_ARM), ik().get("Enabled")), lambda r: r == (True, True), timeout=10)
checks.step("undo Reset → All brings both back", undo.get("ok") and ok,
            f"left arm edited {edited(LEFT_ARM)}, IK {ik().get('Enabled')}")

checks.finish()
