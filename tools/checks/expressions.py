"""Expressions: hold an expression on the face and release it, bake an expression into
the face pose and undo the bake, and move a face slider and undo it."""
from bridge import Checks, get, history, layer, layers, ready, short, wait_until

checks = Checks("expressions")
ready(checks)

expressions = [e for e in get("/emote", actor=0, name="", list=1, kind="Expression").get("matches", [])
               if e["slot"] == "Facial"]
if not checks.step("the expression list has a face expression", len(expressions) > 0, f"{len(expressions)} found"):
    checks.finish()
expression = expressions[0]
timeline = expression["TimelineId"]

held = get("/expression", actor=0, hold=timeline)
ok, reading = wait_until(layers, lambda r: r.get("heldExpression") == timeline
                         and layer(r, "Facial").get("live") == timeline, timeout=5)
checks.step(f"hold {expression['Name']}", held.get("ok") and ok,
            f"held {reading.get('heldExpression')}, face layer plays {layer(reading, 'Facial').get('live')}")

released = get("/expression", actor=0, release=1)
ok, reading = wait_until(layers, lambda r: r.get("heldExpression") is None, timeout=5)
checks.step("release it", released.get("ok") and ok, f"held {reading.get('heldExpression')}")

baked = get("/expressionbake", actor=0, timeline=timeline)
finished, _ = wait_until(lambda: get("/expressionbake", actor=0), lambda r: r.get("baking") is False, timeout=10)
edits = get("/poseedit", actor=0)
step = history().get("undo")
checks.step("bake it into the face pose", baked.get("ok") and finished and edits.get("authored") is True
            and step == "Bake expression into the pose",
            f"bake {baked.get('ok')} {baked.get('Detail')}, finished {finished}, pose edited {edits.get('authored')}, undo step {step}")

undone = get("/undo")
ok, edits = wait_until(lambda: get("/poseedit", actor=0), lambda r: r.get("authored") is False, timeout=5)
checks.step("undo the bake", undone.get("ok") and ok, f"pose edited {edits.get('authored')}")

face = get("/face", actor=0)
units = face.get("units", [])
if checks.step("the face sliders are available", face.get("available") and units, f"{len(units)} sliders"):
    unit = units[0]
    moved = get("/face", actor=0, unit=unit["id"], weight=0.6)
    weight = next((u["weight"] for u in moved.get("units", []) if u["id"] == unit["id"]), None)
    checks.step(f"move {unit['label']} to 0.6", moved.get("ok") and weight is not None and abs(weight - 0.6) < 0.01,
                f"weight {weight}, undo step {moved.get('history', {}).get('undo')}")
    get("/undo")
    ok, reading = wait_until(lambda: get("/face", actor=0),
                             lambda r: any(u["id"] == unit["id"] and abs(u["weight"]) < 0.01 for u in r.get("units", [])),
                             timeout=3)
    checks.step("undo puts the slider back", ok,
                short([u for u in reading.get("units", []) if u["id"] == unit["id"]]))

checks.finish()
