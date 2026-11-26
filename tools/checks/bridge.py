"""What every check script shares: calling the debug bridge, waiting for the game,
and printing PASS or FAIL for each step.

The bridge is the small web server in Debug builds (Poser/Bridge/DebugBridge*.cs).
Every script in this folder imports this file, so run them from any folder with
`python tools/checks/<area>.py`.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("POSER_BRIDGE", "http://127.0.0.1:47999")

# Windows consoles default to a code page that cannot print every character in a reading.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# The layer numbers the bridge takes in slot=. Leaving slot out means Upper body.
FULL_BODY, UPPER_BODY, FACIAL = 0, 1, 2


def get(path, **params):
    """Calls one bridge command and returns its answer as a dictionary or list.
    A request that fails comes back as {"error": ...} so a step can report it."""
    query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
    url = BASE + path + ("?" + query if query else "")
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        try:
            return json.loads(error.read())
        except ValueError:
            return {"error": f"HTTP {error.code}"}
    except (urllib.error.URLError, OSError, ValueError) as error:
        return {"error": str(error)}


def wait_until(read, test, timeout=10.0, interval=0.2):
    """Reads again and again until test(reading) is true or the time runs out.
    Returns (passed, last reading)."""
    end = time.monotonic() + timeout
    while True:
        reading = read()
        if test(reading):
            return True, reading
        if time.monotonic() >= end:
            return False, reading
        time.sleep(interval)


def short(value, limit=300):
    """A bridge answer as one line, cut so a step's line stays readable."""
    text = json.dumps(value, separators=(",", ":")) if not isinstance(value, str) else value
    return text if len(text) <= limit else text[:limit] + "..."


class Checks:
    """Counts the steps of one script and prints each as PASS or FAIL with what it saw."""

    def __init__(self, area):
        self.area = area
        self.failed = []
        self.count = 0

    def step(self, name, passed, saw):
        self.count += 1
        if not passed:
            self.failed.append(name)
        print(f"{'PASS' if passed else 'FAIL'} {name} :: {saw}", flush=True)
        return passed

    def finish(self):
        """Prints the totals and ends the script, with exit code 1 when a step failed."""
        print(f"\n{self.area}: {self.count - len(self.failed)} of {self.count} steps passed")
        for name in self.failed:
            print(f"  failed: {name}")
        sys.exit(1 if self.failed else 0)


# ── Reading the actor ────────────────────────────────────────────────────

def layers(actor=0):
    return get("/layers", actor=actor)


def layer(reading, slot_name):
    """One layer from a /layers reading, by the name the bridge gives it
    (Base is Full body, UpperBody is Upper body)."""
    for entry in reading.get("layers", []):
        if entry["slot"] == slot_name:
            return entry
    return {}


def clock_advance(slot_name="Base", seconds=1.0, actor=0):
    """How far a layer's clock moved in that many seconds, read from /layers.
    A clock that wraps at the end of the animation is counted forward."""
    first = layer(layers(actor), slot_name)
    time.sleep(seconds)
    second = layer(layers(actor), slot_name)
    if first.get("time") is None or second.get("time") is None:
        return None
    moved = second["time"] - first["time"]
    if moved < -0.05 and second.get("duration"):
        moved += second["duration"]
    return round(moved, 3)


def history():
    return get("/history")


def actors():
    return get("/actors").get("actors", [])


# ── Getting ready ────────────────────────────────────────────────────────

def ready(checks, actor=0):
    """Waits for the bridge and the animation list, then puts the actor back to the
    game's state with Reset → All, so each script starts from the same place."""
    ok, reading = wait_until(lambda: get("/actors"), lambda r: "actors" in r, timeout=30, interval=1)
    if not checks.step("bridge answers", ok and len(reading.get("actors", [])) > 0, short(reading)):
        checks.finish()
    ok, reading = wait_until(lambda: get("/emote", actor=actor, name="Sit", list=1),
                             lambda r: "matches" in r, timeout=60, interval=1)
    if not checks.step("animation list loaded", ok, short(reading)):
        checks.finish()
    remove_copies(checks)
    reset_all(checks, actor)


def reset_all(checks, actor=0):
    """Reset → All, then waits until the actor plays the game's own animation again."""
    result = get("/resetall", actor=actor)
    settled, reading = wait_until(lambda: get("/state", actor=actor),
                                  lambda s: s.get("stance") == "Idle" and not s.get("paused"), timeout=10)
    return checks.step("Reset → All puts the actor back", result.get("ok") is True and settled,
                       f"reset {short(result, 160)} stance {reading.get('stance')} paused {reading.get('paused')}")


def remove_copies(checks):
    """Removes every actor but the first, the way the Remove button does."""
    extra = [a for a in actors()[1:]]
    if not extra:
        return
    get("/selection", entities=",".join(f"actor:{a['id']}" for a in extra))
    get("/selectionremove", start=1)
    ok, reading = wait_until(lambda: get("/selectionremove"), lambda r: r.get("running") is False, timeout=20)
    left = actors()
    checks.step("copies from earlier runs removed", len(left) == 1, f"{len(extra)} removed, {len(left)} actors left")


def find_emote(name, slot=None):
    """The emote with exactly that name from the General section's list, so scripts
    don't depend on timeline numbers. Returns (emote, pick) or (None, None)."""
    matches = get("/emote", actor=0, name=name, list=1).get("matches", [])
    exact = [m for m in matches if m["Name"].lower() == name.lower()]
    for pick, emote in enumerate(exact):
        if slot is None or emote["slot"] == slot:
            return emote, pick
    return None, None


def play_emote(name, actor=0, slot=None):
    """Plays an emote the way the General section's Apply does, and returns its answer."""
    emote, pick = find_emote(name, slot)
    if emote is None:
        return {"ok": False, "Detail": f"No emote is called {name}."}
    return get("/emote", actor=actor, name=name, pick=pick)
