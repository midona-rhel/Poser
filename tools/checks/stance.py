"""Stance and weapon: each stance, the weapon drawn and sheathed, undo of the weapon,
and Lock position switched on and off."""
from bridge import Checks, get, ready, wait_until

checks = Checks("stance and weapon")
ready(checks)


def state():
    return get("/state", actor=0)


for stance in ["SitGround", "SitChair", "Sleeping", "Idle"]:
    answer = get("/stance", actor=0, stance=stance)
    ok, reading = wait_until(state, lambda s: s.get("stance") == stance, timeout=6)
    checks.step(f"stance {stance}", answer.get("ok") and ok, f"answer {answer.get('ok')} {answer.get('Detail')}, now {reading.get('stance')}")

for drawn in [True, False, True]:
    answer = get("/weaponstate", actor=0, drawn=int(drawn))
    ok, reading = wait_until(state, lambda s: s.get("weaponDrawn") is drawn, timeout=6)
    checks.step(f"weapon {'drawn' if drawn else 'sheathed'}", answer.get("accepted") and ok,
                f"answer {answer.get('accepted')} {answer.get('reason')}, drawn {reading.get('weaponDrawn')}")

undo = get("/undo")
ok, reading = wait_until(state, lambda s: s.get("weaponDrawn") is False, timeout=6)
checks.step("undo sheathes the weapon again", undo.get("ok") and ok,
            f"undid {undo.get('history', {}).get('redo')}, drawn {reading.get('weaponDrawn')}")
redo = get("/redo")
ok, reading = wait_until(state, lambda s: s.get("weaponDrawn") is True, timeout=6)
checks.step("redo draws it again", redo.get("ok") and ok, f"drawn {reading.get('weaponDrawn')}")

locked = get("/positionlock", actor=0, on=1)
checks.step("Lock position on", locked.get("ok") and locked.get("locked") is True, f"{locked}")
checks.step("Lock position reads as on", get("/positionlock", actor=0).get("locked") is True, "read without on")
unlocked = get("/positionlock", actor=0, on=0)
checks.step("Lock position off", unlocked.get("ok") and unlocked.get("locked") is False, f"{unlocked}")

checks.finish()
