"""Environment and lights: switch the time's freeze and set the time of day with undo, and
change a light's switch, colour and intensity with undo."""
from bridge import Checks, add_light, get, ready, short, wait_until

checks = Checks("environment and lights")
ready(checks)


def environment():
    return get("/environment")


# Freezing time that is already frozen changes nothing and makes no undo step, so the
# freeze is first switched to the opposite of what it is now.
start = environment()
was_frozen = bool(start.get("timeFrozen"))
switched = get("/environment", frozen=int(not was_frozen))
ok, reading = wait_until(environment, lambda e: bool(e.get("timeFrozen")) is not was_frozen, timeout=3)
checks.step("freeze" if not was_frozen else "release the time", ok, f"frozen {reading.get('timeFrozen')}")
get("/undo")
ok, reading = wait_until(environment, lambda e: bool(e.get("timeFrozen")) is was_frozen, timeout=3)
checks.step("undo puts the freeze back", ok, f"frozen {reading.get('timeFrozen')}")

# Setting the time freezes it, so undo has to let it run again (#841).
get("/environment", frozen=0)
start = environment()
evening = 18 * 60 if abs(start.get("minuteOfDay", 0) - 18 * 60) > 30 else 6 * 60
timed = get("/environment", minute=evening)
ok, reading = wait_until(environment, lambda e: abs(e.get("minuteOfDay", -999) - evening) <= 1, timeout=3)
checks.step(f"set the time to {evening // 60}:00", ok and reading.get("timeFrozen") is True,
            f"was {start.get('minuteOfDay')}, now {reading.get('minuteOfDay')}, frozen {reading.get('timeFrozen')}")
get("/undo")
ok, reading = wait_until(environment, lambda e: abs(e.get("minuteOfDay", -999) - evening) > 5, timeout=3)
checks.step("undo moves the time back", ok, f"now {reading.get('minuteOfDay')}")
checks.step("undo lets the time run again", reading.get("timeFrozen") is False, f"frozen {reading.get('timeFrozen')}")

light = add_light()
if checks.step("add a light", light, str(light)):
    def read():
        return get("/light", light=light).get("light") or {}

    off = get("/light", light=light, property="IsOn", value=0)
    checks.step("switch the light off", off.get("ok") and read().get("IsOn") is False, f"undo step {off.get('history', {}).get('undo')}")
    get("/undo")
    checks.step("undo switches it on", read().get("IsOn") is True, "")

    colour = get("/light", light=light, property="Color", value="1,0.5,0.25")
    c = read().get("Color", {})
    checks.step("colour it orange", colour.get("ok") and abs(c.get("Y", 0) - 0.5) < 0.01, short(c))

    bright = get("/light", light=light, property="Intensity", value=7)
    checks.step("intensity 7", bright.get("ok") and abs(read().get("Intensity", 0) - 7) < 0.01, f"{read().get('Intensity')}")
    get("/undo")
    checks.step("undo the intensity", abs(read().get("Intensity", 0) - 7) > 0.01, f"{read().get('Intensity')}")
    get("/parenting", child=light, remove=1)

checks.finish()
