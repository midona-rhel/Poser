"""Appearance: the actor's opacity, tint, wetness and visibility in the Appearance tab,
each with undo, and Reset → All taking them back to the game's values."""
from bridge import Checks, get, ready, short, wait_until

checks = Checks("appearance")
ready(checks)


def presentation():
    return get("/presentation", actor=0)


start = presentation()
if not checks.step("read the actor's presentation", start.get("opacity") is not None and start.get("visible") is True,
                   short(start, 200)):
    checks.finish()

faded = get("/presentation", actor=0, opacity=0.5)
ok, reading = wait_until(presentation, lambda p: p.get("opacity") is not None and abs(p["opacity"] - 0.5) < 0.01, timeout=3)
checks.step("opacity 0.5", faded.get("ok") and ok, f"opacity {reading.get('opacity')}, undo step {faded.get('history', {}).get('undo')}")
get("/undo")
ok, reading = wait_until(presentation, lambda p: p.get("opacity") is not None and abs(p["opacity"] - start["opacity"]) < 0.01, timeout=3)
checks.step("undo the opacity", ok, f"opacity {reading.get('opacity')}")

tinted = get("/presentation", actor=0, tint="1,0.2,0.2,1", model="Character")
ok, reading = wait_until(presentation, lambda p: p.get("tint") and abs(p["tint"][1] - 0.2) < 0.01, timeout=3)
checks.step("tint the actor red", tinted.get("ok") and ok, f"tint {reading.get('tint')}")

# As in the Appearance tab, the wetness is held first and the sliders then set it. The
# target is the opposite of the game's own wetness, which is already high in rain.
game_wetness = (start.get("wetness") or {}).get("Weather", 0)
target = 0 if game_wetness > 0.5 else 1
wet = get("/presentation", actor=0, wet=1, wetness=f"{target},0,0")
ok, reading = wait_until(presentation, lambda p: abs((p.get("wetness") or {}).get("Weather", -1) - target) < 0.1, timeout=3)
checks.step(f"set the wetness to {target}", wet.get("ok") and wet.get("wet") is True and ok,
            f"game's wetness {game_wetness}, held {wet.get('wet')}, wetness {short(reading.get('wetness'))}")

hidden = get("/presentation", actor=0, visible=0)
ok, reading = wait_until(presentation, lambda p: p.get("visible") is False, timeout=3)
checks.step("hide the actor", hidden.get("ok") and ok, f"visible {reading.get('visible')}")
get("/undo")
ok, reading = wait_until(presentation, lambda p: p.get("visible") is True, timeout=3)
checks.step("undo shows it again", ok, f"visible {reading.get('visible')}")

reset = get("/resetall", actor=0)
ok, reading = wait_until(presentation, lambda p: p.get("wet") is False and p.get("ownedOpacity") is None
                         and p.get("tint") == start.get("tint")
                         and abs((p.get("wetness") or {}).get("Weather", -1) - game_wetness) < 0.1, timeout=10)
checks.step("Reset → All takes tint and wetness back to the game's", reset.get("ok") and ok, short(reading, 200))

checks.finish()
