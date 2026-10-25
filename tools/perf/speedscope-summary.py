"""Sum a dotnet-trace speedscope export per frame name.

Capture and convert:
    dotnet-trace collect -p <ffxiv_dx11 pid> --profile dotnet-sampled-thread-time --duration 00:00:45 -o t.nettrace
    dotnet-trace convert t.nettrace --format Speedscope -o t

Then:
    python tools/perf/speedscope-summary.py t.speedscope.json [substring ...]

With no substrings, print the top Poser frames by inclusive time and the
number of outermost UIManager.DrawUI openings in the trace, which is used
as the frame count. This script assumes millisecond event timestamps and a
45-second recording when printing FPS. With substrings, print matching frames and their immediate child
frames. Sampled native work is commonly attributed to UNMANAGED child frames,
so inspect the children when locating a cost.

Inclusive time includes child calls. Work in another plugin's hook can therefore
appear beneath Poser's detour frame; that time is not necessarily spent in Poser.
"""
import collections
import json
import sys


def main(path, wanted):
    data = json.load(open(path))
    names = [f["name"] for f in data["shared"]["frames"]]
    draw = {i for i, n in enumerate(names) if "UIManager.DrawUI()" in n}
    targets = {w: {i for i, n in enumerate(names) if w in n} for w in wanted}
    incl = collections.Counter()
    children = {w: collections.Counter() for w in wanted}
    frames = 0
    for profile in data["profiles"]:
        if profile["type"] != "evented":
            continue
        stack = []
        last = profile["startValue"]
        for event in profile["events"]:
            at = event["at"]
            dt = at - last
            last = at
            if dt > 0 and stack:
                for f in set(stack):
                    incl[f] += dt
                for w, ids in targets.items():
                    for pos in range(len(stack) - 1, -1, -1):
                        if stack[pos] in ids:
                            child = stack[pos + 1] if pos + 1 < len(stack) else stack[pos]
                            children[w][child] += dt
                            break
            if event["type"] == "O":
                stack.append(event["frame"])
                if event["frame"] in draw and not any(s in draw for s in stack[:-1]):
                    frames += 1
            elif event["type"] == "C" and stack:
                stack.pop()
    print(f"frames {frames}  (~{frames / 45:.1f} fps over 45 s)")
    if not wanted:
        ours = [(f, t) for f, t in incl.items() if "Poser" in names[f]]
        for f, t in sorted(ours, key=lambda x: -x[1])[:30]:
            print(f"{t:8.0f} ms  {t / max(1, frames):5.2f} ms/frame  {names[f][:110]}")
        return
    for w, ids in targets.items():
        total = sum(incl[i] for i in ids)
        print(f"== {w}: {total:.0f} ms, {total / max(1, frames):.2f} ms/frame")
        for f, t in children[w].most_common(10):
            print(f"   {t:7.0f} ms  {names[f][:115]}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
