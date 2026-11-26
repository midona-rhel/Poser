"""Scrubbing: scrub Full body to a time and see it stay there, scrub again, scrub with
the backup clock write, seek by the layer's own clock, undo a seek, and play again."""
import time

from bridge import Checks, FULL_BODY, get, layer, layers, ready, wait_until

checks = Checks("scrubbing")
ready(checks)


def base():
    return layer(layers(), "Base")


def at(seconds):
    return lambda b: b.get("time") is not None and abs(b["time"] - seconds) < 0.02


answer = get("/scrub", actor=0, slot=FULL_BODY, time=0.5)
ok, reading = wait_until(base, at(0.5), timeout=3)
whole = layers()
checks.step("scrub Full body to 0.5 s", answer.get("ok") and ok and not whole["paused"],
            f"clock {reading.get('time')}, Full body speed {reading.get('speed')}, actor paused {whole['paused']}")
time.sleep(1)
later = base()
checks.step("it stays at 0.5 s", at(0.5)(later), f"clock a second later {later.get('time')}")

answer = get("/scrub", actor=0, slot=FULL_BODY, time=1.2)
ok, reading = wait_until(base, at(1.2), timeout=3)
checks.step("scrub again to 1.2 s", answer.get("ok") and ok, f"clock {reading.get('time')}")

get("/scrubfallback", on=1)
answer = get("/scrub", actor=0, slot=FULL_BODY, time=0.8)
ok, reading = wait_until(base, at(0.8), timeout=3)
checks.step("backup clock write to 0.8 s", answer.get("ok") and ok, f"clock {reading.get('time')} {answer.get('Detail')}")
get("/scrubfallback", on=0)
checks.step("the backup write is off again", get("/scrubfallback").get("fallback") is False, "read without on")

before_seek = base().get("time")
answer = get("/seeklayer", actor=0, slot=FULL_BODY, time=2.0)
ok, reading = wait_until(base, lambda b: b.get("time") is not None and abs(b["time"] - 2.0) < 0.05, timeout=3)
checks.step("seek by the layer's own clock to 2.0 s", answer.get("ok") and ok, f"clock {reading.get('time')} {answer.get('Detail')}")

undo = get("/undo")
ok, reading = wait_until(base, lambda b: b.get("time") is not None and abs(b["time"] - 2.0) > 0.05, timeout=3)
checks.step("undo goes back to the earlier frame", undo.get("ok") and ok,
            f"clock {reading.get('time')} (before the seek {before_seek}), undid {undo.get('history', {}).get('redo')}")

get("/play", actor=0, slot=FULL_BODY)
first = base().get("time")
time.sleep(1)
second = base().get("time")
checks.step("play the layer again", first is not None and second is not None and first != second,
            f"clock {first} then {second}")

checks.finish()
