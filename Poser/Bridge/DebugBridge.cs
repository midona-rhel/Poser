#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Bridge;

/// <summary>
/// THE DEBUG BRIDGE — a Debug-build-only local HTTP control surface so the
/// animation work can be driven and measured from outside the game (the MCP
/// server in tools/ wraps it). Every game call is marshalled onto the
/// framework thread; every answer is JSON. Not a feature: a test rig.
/// </summary>
public sealed class DebugBridge : IDisposable
{
    public const int Port = 47999;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly AnimationSession _animation;
    private readonly Game.Animation.AnimationRuntimePort _port;
    private readonly IActorManager _actors;
    private readonly StableBindingRegistry _bindings;
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly IActorSpawnService _spawner;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    public DebugBridge(
        IFramework framework,
        IPluginLog log,
        AnimationSession animation,
        Game.Animation.AnimationRuntimePort port,
        IActorManager actors,
        StableBindingRegistry bindings,
        Game.Scene.SceneLifecycleHistory lifecycle,
        IActorSpawnService spawner)
    {
        _framework = framework;
        _log = log;
        _animation = animation;
        _port = port;
        _actors = actors;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _spawner = spawner;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
            _log.Information($"[Bridge] listening on http://127.0.0.1:{Port}/");
        }
        catch (Exception ex)
        {
            _log.Error($"[Bridge] could not listen on {Port}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[16384];
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, _stop.Token);
                if (read <= 0)
                    return;
                var request = Encoding.UTF8.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n", 2)[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2)
                    return;
                var url = parts[1];
                int q = url.IndexOf('?');
                string path = q < 0 ? url : url[..q];
                var query = ParseQuery(q < 0 ? string.Empty : url[(q + 1)..]);
                string body;
                int status = 200;
                try
                {
                    body = await Route(path, query);
                }
                catch (Exception ex)
                {
                    status = 500;
                    body = Json(new { error = ex.GetType().Name, message = ex.Message });
                }
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} OK\r\nContent-Type: application/json\r\n"
                    + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, 0, header.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                _log.Warning($"[Bridge] request failed: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "1" : pair[(eq + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });

    // ── Routing ─────────────────────────────────────────────────────────

    private Task<string> Route(string path, Dictionary<string, string> query)
    {
        switch (path)
        {
            case "/":
            case "/help":
                return Task.FromResult(Json(new
                {
                    endpoints = new[]
                    {
                        "/actors",
                        "/state?actor=NAME|INDEX",
                        "/apply?actor&slot=1&timeline=8136",
                        "/play?actor&slot=1", "/pause?actor&slot=1",
                        "/pauseall?actor", "/resumeall?actor",
                        "/scrub?actor&slot=1&time=SECONDS",
                        "/loop?actor&slot=1&on=1|0",
                        "/watch?actor", "/dump?actor", "/findclocks?actor",
                        "/clone?actor",
                        "/log?lines=200&filter=REGEX",
                    },
                }));
            case "/log":
                return Task.FromResult(TailLog(query));
            default:
                return _framework.RunOnFrameworkThread(() => RouteOnFramework(path, query));
        }
    }

    private string RouteOnFramework(string path, Dictionary<string, string> query)
    {
        switch (path)
        {
            case "/actors":
                return Json(ListActors());
        }

        if (!query.TryGetValue("actor", out var actorKey))
            return Json(new { error = "actor is required" });
        var actor = FindActor(actorKey);
        if (actor == null)
            return Json(new { error = $"no actor matches '{actorKey}'" });
        if (_bindings.GetActorId(actor) is not { } id)
            return Json(new { error = "actor has no stable id" });

        AnimationSlot Slot() => query.TryGetValue("slot", out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (AnimationSlot)v : AnimationSlot.UpperBody;

        switch (path)
        {
            case "/state":
                return Json(State(id, actor));
            case "/apply":
            {
                if (!query.TryGetValue("timeline", out var tl)
                    || !ushort.TryParse(tl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeline))
                    return Json(new { error = "timeline is required" });
                var chosen = _animation.ChooseSlot(id, Slot(), timeline);
                if (!chosen.Success)
                    return Json(new { ok = false, step = "choose", chosen.Detail });
                var staged = _animation.PlaySelectedSlot(id, Slot(), null, false, resume: false);
                return Json(new { ok = staged.Success, step = "stage", staged.Detail, state = State(id, actor) });
            }
            case "/play":
            {
                var r = _animation.PlaySelectedSlot(id, Slot(), null, false, resume: true);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/pause":
            {
                var r = _animation.PauseSlot(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/pauseall":
            {
                var r = _animation.Pause(id);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/resumeall":
            {
                var r = _animation.Resume(id);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/scrub":
            {
                if (!query.TryGetValue("time", out var t)
                    || !float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    return Json(new { error = "time (seconds) is required" });
                var control = _animation.FindSlotControl(id, Slot());
                if (control == null)
                    return Json(new { ok = false, error = "no live control on that slot" });
                var begun = _animation.BeginScrub(id, control.Id);
                if (!begun.Success)
                    return Json(new { ok = false, step = "begin", begun.Detail });
                var moved = _animation.UpdateScrub(id, time);
                _animation.EndScrub();
                return Json(new { ok = moved.Success, moved.Detail, state = State(id, actor) });
            }
            case "/loop":
            {
                bool on = !query.TryGetValue("on", out var o) || o != "0";
                ushort timeline = _animation.SelectedFor(id, Slot())
                    ?? _animation.Read(id)?.TimelineFor(Slot()) ?? 0;
                var r = _animation.SetSlotLoop(id, Slot(), timeline, on);
                return Json(new { ok = r.Success, r.Detail, timeline, state = State(id, actor) });
            }
            case "/watch":
                _port.ProbeWatchReset(id);
                return Json(new { ok = true });
            case "/dump":
                _port.ProbeDump(id);
                return Json(new { ok = true });
            case "/findclocks":
                _port.ProbeFindClocks(id);
                return Json(new { ok = true });
            case "/clone":
            {
                var clone = _lifecycle.SpawnActor($"Bridge clone of {actor.Name}", () => _spawner.CloneActor(actor));
                return Json(new { ok = clone != null, name = clone?.Name, id = clone != null ? _bindings.GetActorId(clone)?.ToString() : null });
            }
        }
        return Json(new { error = $"unknown endpoint {path}" });
    }

    // ── Helpers (framework thread) ──────────────────────────────────────

    private object ListActors()
    {
        var list = new List<object>();
        int index = 0;
        foreach (var actor in _actors.Actors)
        {
            var id = _bindings.GetActorId(actor);
            list.Add(new { index, actor.Name, id = id?.ToString(), paused = id is { } aid && _animation.IsPaused(aid) });
            index++;
        }
        return new { actors = list };
    }

    private IActor? FindActor(string key)
    {
        var actors = _actors.Actors;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < actors.Count)
            return actors[index];
        IActor? partial = null;
        foreach (var actor in actors)
        {
            if (string.Equals(actor.Name, key, StringComparison.OrdinalIgnoreCase))
                return actor;
            if (partial == null && actor.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                partial = actor;
        }
        return partial;
    }

    private object State(ActorId id, IActor actor)
    {
        var reading = _animation.Read(id);
        var owned = _animation.OverridesFor(id);
        var snapshot = _port.ProbeSnapshot(id);
        var slots = new List<object>();
        if (reading != null)
        {
            foreach (var slot in reading.Slots)
            {
                if (slot.TimelineId == 0 && Math.Abs(slot.Speed - 1f) < 0.001f)
                    continue;
                slots.Add(new { slot = (int)slot.Slot, name = slot.Slot.ToString(), timeline = slot.TimelineId, speed = slot.Speed });
            }
        }
        var controls = new List<object>();
        if (reading != null)
        {
            foreach (var control in reading.Controls)
                controls.Add(new { partial = control.Id.Partial, control = control.Id.Control, time = control.Time, duration = control.Duration, speed = control.PlaybackSpeed });
        }
        return new
        {
            actor = actor.Name,
            id = id.ToString(),
            paused = _animation.IsPaused(id),
            anyPlaying = _animation.AnyPlaying(id),
            overallSpeed = reading?.OverallSpeed,
            baseOverride = reading?.BaseTimeline,
            emoteId = snapshot?.EmoteId,
            mode = snapshot?.Mode,
            schedulerFrames = snapshot?.SchedulerFrames,
            childFrames = snapshot?.ChildFrames,
            slots,
            controls,
            owned = new
            {
                baseTimeline = owned.BaseTimeline,
                selected = owned.SelectedSlots,
                looped = owned.LoopedSlots,
                slotSpeeds = owned.SlotSpeeds,
                overallSpeed = owned.OverallSpeed,
            },
        };
    }

    private static string TailLog(Dictionary<string, string> query)
    {
        int lines = query.TryGetValue("lines", out var l)
            && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 200;
        string? filter = query.TryGetValue("filter", out var f) && f.Length > 0 ? f : null;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "dalamud.log");
        var all = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                all.Add(line);
        }
        var selected = new List<string>();
        var regex = filter != null ? new System.Text.RegularExpressions.Regex(filter) : null;
        for (int i = all.Count - 1; i >= 0 && selected.Count < lines; i--)
        {
            if (regex == null || regex.IsMatch(all[i]))
                selected.Add(all[i]);
        }
        selected.Reverse();
        return Json(new { lines = selected });
    }
}
#endif
