"""Speed: the whole actor's speed, pause and resume, one layer's speed, pause and
reset, and undo and redo of a layer reset. Each step reads Full body's clock from
/layers and measures how far it moves in one second."""
import time

from bridge import Checks, FULL_BODY, clock_advance, get, layer, layers, ready

checks = Checks("speed")
ready(checks)


def step(name, path, params, expect, describe):
    answer = get(path, **params)
    # A change in speed reaches the clock on a later frame.
    time.sleep(0.3)
    moved = clock_advance("Base", 1.0)
    reading = layers()
    base = layer(reading, "Base")
    checks.step(name, expect(moved, reading, base, answer),
                f"{describe}; moved {moved} s in 1 s, paused {reading.get('paused')}, "
                f"actor speed {reading.get('overallSpeed')}, Full body speed {base.get('speed')}"
                + ("" if answer.get("ok", True) is not False else f", refused: {answer.get('Detail')}"))


full = lambda m: m is not None and m > 0.8
half = lambda m: m is not None and 0.35 < m < 0.65
still = lambda m: m is not None and abs(m) < 0.02

step("plays at normal speed", "/history", {}, lambda m, r, b, a: full(m), "Full body moves about 1 s per second")
step("pause all", "/pauseall", {"actor": 0}, lambda m, r, b, a: still(m) and r["paused"], "the clock stops")
step("resume all", "/resumeall", {"actor": 0}, lambda m, r, b, a: full(m) and not r["paused"], "the clock runs at full speed")

step("whole actor at 0.5", "/actorspeed", {"actor": 0, "value": 0.5},
     lambda m, r, b, a: half(m) and r["overallSpeed"] == 0.5, "the Speed slider for the whole actor")
step("whole actor speed cleared", "/actorspeed", {"actor": 0, "clear": 1},
     lambda m, r, b, a: full(m) and r["overallSpeed"] is None, "speed goes back to the game")

step("Full body at 0.5", "/speed", {"actor": 0, "slot": FULL_BODY, "value": 0.5},
     lambda m, r, b, a: half(m) and b["speed"] == 0.5, "only Full body's speed changes")
step("pause all on top of 0.5", "/pauseall", {"actor": 0},
     lambda m, r, b, a: still(m), "a layer with its own speed stops too")
step("resume all keeps 0.5", "/resumeall", {"actor": 0},
     lambda m, r, b, a: half(m), "back to half speed")
step("clear Full body's speed", "/clearspeed", {"actor": 0, "slot": FULL_BODY},
     lambda m, r, b, a: full(m) and b["speed"] is None, "full speed, nothing held")

step("pause one layer", "/pause", {"actor": 0, "slot": FULL_BODY},
     lambda m, r, b, a: still(m) and not r["paused"], "only that layer stops, the actor is not paused")
step("play that layer", "/play", {"actor": 0, "slot": FULL_BODY},
     lambda m, r, b, a: full(m), "it runs again")

step("Full body at 0.5 again", "/speed", {"actor": 0, "slot": FULL_BODY, "value": 0.5},
     lambda m, r, b, a: half(m), "half speed")
step("reset the layer", "/reset", {"actor": 0, "slot": FULL_BODY},
     lambda m, r, b, a: full(m) and b["speed"] is None, "full speed, nothing held")
step("undo the reset", "/undo", {},
     lambda m, r, b, a: half(m) and b["speed"] == 0.5 and not r["paused"], "half speed again, and not paused")
step("redo the reset", "/redo", {},
     lambda m, r, b, a: full(m) and b["speed"] is None, "full speed again")

checks.finish()
