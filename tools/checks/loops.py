"""Loops: an Upper body loop held for 20 seconds while seated, a looped layer the game
cancels is played again, a layer with its loop off stays stopped, scrubbing a looped
layer while paused, and Full body repeat."""
import time

from bridge import Checks, FULL_BODY, UPPER_BODY, get, layer, layers, play_emote, ready, reset_all, wait_until

checks = Checks("loops")
ready(checks)


def live(slot_name):
    return layer(layers(), slot_name).get("live")


sit = play_emote("Sit")
ok, state = wait_until(lambda: get("/state", actor=0), lambda s: s.get("stance") == "SitChair", timeout=8)
checks.step("sit down", sit.get("ok") and ok, f"emote {sit.get('ok')} {sit.get('Detail')}, stance {state.get('stance')}")

think = play_emote("Think")
upper = think.get("played")
ok, _ = wait_until(lambda: live("UpperBody"), lambda t: t == upper, timeout=5)
armed = get("/loop", actor=0, slot=UPPER_BODY, on=1)
checks.step("loop Upper body while seated", armed.get("ok") and armed.get("timeline") == upper and ok,
            f"Think plays {upper} on {think.get('slot')}, loop answer ok {armed.get('ok')} timeline {armed.get('timeline')}")
checks.step("the loop reads as on", get("/loop", actor=0, slot=UPPER_BODY).get("loop") is True, "read without on")

seen = []
for _ in range(10):
    time.sleep(2)
    seen.append(live("UpperBody"))
checks.step("Upper body keeps looping for 20 s", all(t == upper for t in seen), f"Upper body every 2 s: {seen}")

def upper_in(state):
    """Upper body's timeline in a /state reading, read on the same frame as the command."""
    return next((s["timeline"] for s in state.get("slots", []) if s["slot"] == UPPER_BODY), None)


cancelled = get("/cancel", actor=0, a2=1)
right_after = upper_in(cancelled.get("state", {}))
ok, again = wait_until(lambda: live("UpperBody"), lambda t: t == upper, timeout=4)
checks.step("a cancelled looped layer is played again", right_after != upper and ok,
            f"on the frame of the cancel {right_after}, then {again}")

get("/loop", actor=0, slot=UPPER_BODY, on=0)
time.sleep(0.5)
get("/cancel", actor=0, a2=1)
ok, after = wait_until(lambda: live("UpperBody"), lambda t: t in (None, 0), timeout=4)
checks.step("with the loop off, a cancelled layer stays stopped", ok, f"Upper body {after}")

play_emote("Think")
wait_until(lambda: live("UpperBody"), lambda t: t == upper, timeout=5)
get("/loop", actor=0, slot=UPPER_BODY, on=1)
get("/pauseall", actor=0)
scrub = get("/scrub", actor=0, slot=UPPER_BODY, time=0.5)
ok, reading = wait_until(layers, lambda r: abs((layer(r, "UpperBody").get("time") or -1) - 0.5) < 0.02, timeout=3)
checks.step("scrub a looped layer while paused", scrub.get("ok") and ok,
            f"scrub {scrub.get('ok')} {scrub.get('Detail')}, Upper body clock {layer(reading, 'UpperBody').get('time')}")
get("/resumeall", actor=0)
time.sleep(4)
checks.step("resume keeps the loop", live("UpperBody") == upper and layer(layers(), "UpperBody").get("loopWanted"),
            f"Upper body {live('UpperBody')}")
get("/loop", actor=0, slot=UPPER_BODY, on=0)

# The stance control stands the actor up.
stand = get("/stance", actor=0, stance="Idle")
ok, state = wait_until(lambda: get("/state", actor=0), lambda s: s.get("stance") == "Idle", timeout=8)
checks.step("stand up with the stance control", stand.get("ok") and ok,
            f"stance {stand.get('ok')} {stand.get('Detail')}, now {state.get('stance')}")
# Reset → All after Sit and a stance change leaves the actor standing (#839).
reset_all(checks)
photo = play_emote("Photograph")
full = photo.get("played")
ok, _ = wait_until(lambda: live("Base"), lambda t: t == full, timeout=5)
repeat = get("/loop", actor=0, slot=FULL_BODY, on=1)
seen = []
for _ in range(6):
    time.sleep(2.5)
    seen.append(live("Base"))
checks.step("Full body repeat holds for 15 s", repeat.get("ok") and all(t == full for t in seen),
            f"Photograph plays {full}, Full body every 2.5 s: {seen}")
get("/loop", actor=0, slot=FULL_BODY, on=0)

checks.finish()
