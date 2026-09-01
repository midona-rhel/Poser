#!/usr/bin/env python3
"""Poser debug MCP server: a stdio JSON-RPC bridge onto the plugin's local
debug HTTP surface (Poser/Debug/DebugBridge.cs, Debug builds only).
Zero dependencies. Tools map 1:1 onto bridge endpoints."""
import json, sys, urllib.request, urllib.parse

BASE = "http://127.0.0.1:47999"

TOOLS = [
    ("poser_actors", "List scene actors (index, name, id, paused).", {}),
    ("poser_state", "Full animation state of an actor: slots, controls, clocks, owned record.",
     {"actor": "name or index"}),
    ("poser_apply", "Stage a timeline on a slot (Apply: loads frozen, does not play).",
     {"actor": "name or index", "slot": "0 base, 1 upper body", "timeline": "timeline id"}),
    ("poser_play", "Play one layer (only that layer moves).", {"actor": "", "slot": ""}),
    ("poser_pause", "Pause one layer.", {"actor": "", "slot": ""}),
    ("poser_pauseall", "Pause the whole actor.", {"actor": ""}),
    ("poser_resumeall", "Play everything on the actor.", {"actor": ""}),
    ("poser_scrub", "Scrub a slot to a time in seconds (pauses the actor).",
     {"actor": "", "slot": "", "time": "seconds"}),
    ("poser_loop", "Arm or disarm the loop on a slot.", {"actor": "", "slot": "", "on": "1 or 0"}),
    ("poser_watch", "Arm the 60s per-frame clock watch on an actor.", {"actor": ""}),
    ("poser_dump", "Log a full native dump of the actor.", {"actor": ""}),
    ("poser_findclocks", "Run the clock hunt on the actor.", {"actor": ""}),
    ("poser_clone", "Clone the actor.", {"actor": ""}),
    ("poser_log", "Tail dalamud.log (lines, optional regex filter).",
     {"lines": "count", "filter": "regex"}),
]

def call(path, params):
    q = urllib.parse.urlencode({k: v for k, v in params.items() if v not in (None, "")})
    url = f"{BASE}{path}" + (f"?{q}" if q else "")
    with urllib.request.urlopen(url, timeout=30) as r:
        return r.read().decode("utf-8")

def tool_schema(name, desc, props):
    return {
        "name": name,
        "description": desc,
        "inputSchema": {
            "type": "object",
            "properties": {k: {"type": "string", "description": v} for k, v in props.items()},
        },
    }

def handle(msg):
    method = msg.get("method")
    mid = msg.get("id")
    if method == "initialize":
        return {"jsonrpc": "2.0", "id": mid, "result": {
            "protocolVersion": "2024-11-05",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "poser-debug", "version": "0.1"}}}
    if method == "notifications/initialized":
        return None
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": mid,
                "result": {"tools": [tool_schema(n, d, p) for n, d, p in TOOLS]}}
    if method == "tools/call":
        name = msg["params"]["name"]
        args = msg["params"].get("arguments", {}) or {}
        path = "/" + name.removeprefix("poser_")
        try:
            text = call(path, args)
        except Exception as e:
            text = json.dumps({"error": str(e)})
        return {"jsonrpc": "2.0", "id": mid,
                "result": {"content": [{"type": "text", "text": text}]}}
    if mid is not None:
        return {"jsonrpc": "2.0", "id": mid,
                "error": {"code": -32601, "message": f"unknown method {method}"}}
    return None

def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        reply = handle(msg)
        if reply is not None:
            sys.stdout.write(json.dumps(reply) + "\n")
            sys.stdout.flush()

if __name__ == "__main__":
    main()
