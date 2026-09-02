using System.Linq;
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
    private readonly global::Poser.Application.Integration.IIntegrationRuntimePort _integration;
    private readonly global::Poser.Application.Integration.ActorIntegrationSession _session;
    private readonly global::Poser.Services.ISkeletonService _skeletons;
    private readonly global::Poser.Services.IGazeService _gaze;
    private readonly global::Poser.Game.WorldObjects.WorldObjectService _worldObjects;
    private readonly global::Poser.Services.ISpawnCatalogService _catalog;
    private readonly global::Poser.Game.Posing.IkBakeCapture _ikBake;
    private readonly global::Poser.Services.IBonePosingService _bonePosing;
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
        IActorSpawnService spawner,
        global::Poser.Application.Integration.IIntegrationRuntimePort integration,
        global::Poser.Application.Integration.ActorIntegrationSession session,
        global::Poser.Services.ISkeletonService skeletons,
        global::Poser.Services.IGazeService gaze,
        global::Poser.Services.IBonePosingService bonePosing,
        global::Poser.Game.WorldObjects.WorldObjectService worldObjects,
        global::Poser.Services.ISpawnCatalogService catalog,
        global::Poser.Game.Posing.IkBakeCapture ikBake)
    {
        _ikBake = ikBake;
        _catalog = catalog;
        _worldObjects = worldObjects;
        _bonePosing = bonePosing;
        _integration = integration;
        _session = session;
        _skeletons = skeletons;
        _gaze = gaze;
        _framework = framework;
        _log = log;
        _animation = animation;
        _port = port;
        _actors = actors;
        _bindings = bindings;
        _lifecycle = lifecycle;
        _spawner = spawner;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        global::Poser.UI.Crystarium.FloatingMenu.Trace = line => _log.Debug(line);
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
                        "/speed?actor&slot=1&value=0.5", "/clearspeed?actor&slot=1",
                        "/reset?actor&slot=1",
                        "/watch?actor", "/dump?actor", "/findclocks?actor",
                        "/clone?actor", "/dupepose?actor",
                        "/log?lines=200&filter=REGEX",
                    },
                }));
            case "/log":
                return Task.FromResult(TailLog(query));
            case "/peek":
                return Task.FromResult(Peek(query));
            case "/poke":
                return Task.FromResult(Poke(query));
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
            case "/speed":
            {
                if (!query.TryGetValue("value", out var sv)
                    || !float.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                    return Json(new { error = "value is required" });
                var r = _animation.SetSlotSpeed(id, Slot(), speed);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/clearspeed":
            {
                var r = _animation.ClearSlotSpeed(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/cancel":
            {
                // Experiment: which cancel arguments stop ONE layer?
                nint a2 = query.TryGetValue("a2", out var x2) ? (nint)long.Parse(x2, CultureInfo.InvariantCulture) : 0;
                nint a3 = query.TryGetValue("a3", out var x3) ? (nint)long.Parse(x3, CultureInfo.InvariantCulture) : 0;
                var r = _port.ProbeCancel(id, a2, a3);
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/reset":
            {
                var r = _animation.ResetSlot(id, Slot());
                return Json(new { ok = r.Success, r.Detail, state = State(id, actor) });
            }
            case "/writelog":
                _port.ProbeSetTimelineLogging(!query.ContainsKey("off"));
                return Json(new { ok = true, logging = _port.ProbeLogging });
            case "/watch":
                _port.ProbeWatchReset(id);
                return Json(new { ok = true });
            case "/dump":
                _port.ProbeDump(id);
                return Json(new { ok = true });
            case "/findclocks":
                _port.ProbeFindClocks(id);
                return Json(new { ok = true });
            case "/setcollection":
            {
                var read = _integration.GetCollectionAssignment(id);
                if (!read.Success || read.Value is not { } cur)
                    return Json(new { error = read.Detail });
                var guid = query.TryGetValue("id", out var g) ? Guid.Parse(g) : cur.EffectiveId;
                var r = _integration.SetIndividualCollection(id, guid);
                return Json(new { ok = r.Success, r.Detail, tried = guid.ToString(), was = $"{cur.EffectiveName} {cur.EffectiveId} individual={cur.HasIndividualAssignment}" });
            }
            case "/resources":
            {
                var paths = _integration.GetActorResourcePaths(id);
                if (!paths.Success || paths.Value is not { } tree)
                    return Json(new { error = paths.Detail });
                var modded = tree.Where(p => !p.Value.Contains(p.Key)).Select(p => p.Key).ToArray();
                if (query.ContainsKey("full"))
                    return Json(new { total = tree.Count, entries = tree.Select(p => new { resolved = p.Key, game = p.Value }).ToArray() });
                return Json(new { total = tree.Count, modded = modded.Length, sample = modded.Take(4).Select(m => m.Length > 90 ? m[^90..] : m).ToArray() });
            }
            case "/bone":
            {
                string name = query.TryGetValue("name", out var bn) ? bn : "j_kosi";
                int wantPartial = query.TryGetValue("partial", out var wp) ? int.Parse(wp) : -1;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && (wantPartial < 0 || bone.PartialId == wantPartial))
                        {
                            var t = bone.LastTransform; var rw = bone.LastRawTransform;
                            return Json(new { name, partial = bone.PartialId, scale = new { t.Scale.X, t.Scale.Y, t.Scale.Z }, position = new { t.Position.X, t.Position.Y, t.Position.Z }, rotation = new { t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W },
                                raw = new { scale = rw.Scale.X, pos = new { rw.Position.X, rw.Position.Y, rw.Position.Z }, rot = new { rw.Rotation.X, rw.Rotation.Y, rw.Rotation.Z, rw.Rotation.W } },
                                modification = _bonePosing.GetModification(bone) is { } m ? new { scale = m.Scale.X, rotW = m.Rotation.W, pos = m.Position.Y } : null });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/bonediff":
            {
                var other = _actors.Actors[int.Parse(query["other"])];
                var mine = new Dictionary<string, (int Partial, global::Poser.Transform T)>();
                foreach (var sk in _skeletons.GetSkeletons(actor))
                    foreach (var b in sk.Bones)
                        mine[$"{b.PartialId}:{b.BoneName}"] = (b.PartialId, b.LastTransform);
                var missing = new List<string>(); var differ = new List<string>(); int same = 0;
                var perPartial = new Dictionary<int, (int Same, int Diff)>();
                foreach (var sk in _skeletons.GetSkeletons(other))
                    foreach (var b in sk.Bones)
                    {
                        string key = $"{b.PartialId}:{b.BoneName}";
                        if (!mine.TryGetValue(key, out var m)) { missing.Add(key); continue; }
                        var t = b.LastTransform;
                        bool eq = (t.Position - m.T.Position).Length() < 0.002f
                            && (t.Scale - m.T.Scale).Length() < 0.002f
                            && MathF.Abs(System.Numerics.Quaternion.Dot(t.Rotation, m.T.Rotation)) > 0.9999f;
                        var pp = perPartial.GetValueOrDefault(b.PartialId);
                        perPartial[b.PartialId] = eq ? (pp.Same + 1, pp.Diff) : (pp.Same, pp.Diff + 1);
                        if (eq) same++; else differ.Add($"{key} dp={(t.Position - m.T.Position).Length():0.000} ds={(t.Scale - m.T.Scale).Length():0.000} dq={1 - MathF.Abs(System.Numerics.Quaternion.Dot(t.Rotation, m.T.Rotation)):0.0000}");
                    }
                var byPartial = differ.GroupBy(x => x.Split(':')[0]).ToDictionary(g => g.Key, g => g.Take(6).ToArray());
                return Json(new { same, differ = differ.Count, missing = missing.Count, perPartial = perPartial.ToDictionary(k => k.Key.ToString(), v => $"{v.Value.Same} same / {v.Value.Diff} diff"), examples = byPartial, missingExamples = missing.Take(6).ToArray() });
            }
            case "/transfer":
            {
                var from = FindActor(query["from"]);
                if (from == null)
                    return Json(new { error = "no such source actor" });
                bool Flag(string key) => !query.TryGetValue(key, out var v) || v != "0";
                _lifecycle.TransferState(from, actor,
                    Flag("rot"), Flag("pos"), Flag("scale"), Flag("physics"), Flag("roots"));
                return Json(new { ok = true, from = from.Name, to = actor.Name });
            }
            case "/bake":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var bp) ? int.Parse(bp) : 0;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            if (_bindings.GetBoneId(bone) is not { } boneId)
                                return Json(new { error = "bone has no binding" });
                            var target = global::Poser.Domain.Identity.TransformTargetId.ForBone(boneId);
                            var begun = _ikBake.Begin(target);
                            return Json(new { ok = begun.Success, begun.Detail, pending = _ikBake.IsPending });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/fonts":
            {
                var typography = global::Poser.UI.Crystarium.ActiveTheme.Typography;
                object Probe(float size)
                {
                    var handle = global::Poser.UI.FontRegistry.Resolve(
                        global::Poser.UI.FontFamily.Mono, global::Poser.UI.FontWeight.Regular, size);
                    return new { size, resolved = handle != null, available = handle?.Available };
                }
                var registry = typeof(global::Poser.UI.FontRegistry);
                const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var directory = registry.GetField("_fontDirectory", Hidden)?.GetValue(null) as string;
                var files = registry.GetField("_files", Hidden)?.GetValue(null) as System.Collections.IDictionary;
                var known = new List<string>();
                if (files != null)
                    foreach (System.Collections.DictionaryEntry entry in files)
                        known.Add($"{entry.Key} => {entry.Value ?? "(default)"}");
                return Json(new { directory, files = known, lastError = global::Poser.UI.FontRegistry.LastError, body = Probe(typography.BodySize), label = Probe(typography.LabelSize), caption = Probe(typography.CaptionSize) });
            }
            case "/bakestate":
                return Json(new { pending = _ikBake.IsPending, note = _ikBake.Note?.Text });
            case "/destroy":
            {
                bool gone = _lifecycle.DespawnActor(actor);
                return Json(new { ok = gone });
            }
            case "/ik":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var ip) ? int.Parse(ip) : 0;
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            var current = _bonePosing.GetIkConfiguration(bone);
                            if (current == null)
                                return Json(new { error = "bone cannot use IK" });
                            var next = current with
                            {
                                Enabled = !query.TryGetValue("enabled", out var en) || en != "0",
                                Solver = query.TryGetValue("solver", out var sv) ? Enum.Parse<global::Poser.Domain.Posing.IkSolver>(sv, true) : current.Solver,
                                CcdDepth = query.TryGetValue("depth", out var dp) ? int.Parse(dp) : current.CcdDepth,
                                CcdIterations = query.TryGetValue("iterations", out var it) ? int.Parse(it) : current.CcdIterations,
                                SwivelDegrees = query.TryGetValue("swivel", out var sw) ? float.Parse(sw, CultureInfo.InvariantCulture) : current.SwivelDegrees,
                            };
                            var error = _bonePosing.SetIkConfiguration(bone, next);
                            return Json(new { ok = error == null, error, solver = next.Solver.ToString(), depth = next.CcdDepth, swivel = next.SwivelDegrees });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/rotatebone":
            {
                string name = query["name"]; int part = query.TryGetValue("partial", out var rp) ? int.Parse(rp) : 0;
                float deg = float.Parse(query["deg"], CultureInfo.InvariantCulture);
                var axis = query.TryGetValue("axis", out var ax) && ax == "x" ? System.Numerics.Vector3.UnitX : ax == "z" ? System.Numerics.Vector3.UnitZ : System.Numerics.Vector3.UnitY;
                var turn = System.Numerics.Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f);
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            var raw = bone.LastRawTransform;
                            float dx = query.TryGetValue("dx", out var dxs) ? float.Parse(dxs, CultureInfo.InvariantCulture) : 0f;
                            float dy = query.TryGetValue("dy", out var dys) ? float.Parse(dys, CultureInfo.InvariantCulture) : 0f;
                            float dz = query.TryGetValue("dz", out var dzs) ? float.Parse(dzs, CultureInfo.InvariantCulture) : 0f;
                            var wanted = new global::Poser.Transform(raw.Position + new System.Numerics.Vector3(dx, dy, dz), System.Numerics.Quaternion.Normalize(raw.Rotation * turn), raw.Scale);
                            _bonePosing.ApplyTransform(bone, wanted, raw);
                            var m = _bonePosing.GetModification(bone);
                            return Json(new { ok = true, modification = m is { } mod ? new { mod.Rotation.X, mod.Rotation.Y, mod.Rotation.Z, mod.Rotation.W } : null });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/scalebone":
            {
                string name = query["name"]; int part = int.Parse(query["partial"]); float sc = float.Parse(query["s"], CultureInfo.InvariantCulture);
                foreach (var skeleton in _skeletons.GetSkeletons(actor))
                    foreach (var bone in skeleton.Bones)
                        if (bone.BoneName == name && bone.PartialId == part)
                        {
                            var raw = bone.LastRawTransform;
                            var wanted = new global::Poser.Transform(raw.Position, raw.Rotation, new System.Numerics.Vector3(sc, sc, sc));
                            _bonePosing.ApplyTransform(bone, wanted, raw);
                            return Json(new { ok = true, raw = new { raw.Scale.X, raw.Rotation.W }, modification = _bonePosing.GetModification(bone)?.Scale.X });
                        }
                return Json(new { error = "no such bone" });
            }
            case "/gazemode":
            {
                var mode = Enum.Parse<GazeTargetMode>(query["mode"], true);
                var r = _gaze.SetGazeMode(actor, mode);
                return Json(new { ok = r.Success, r.Detail, mode = _gaze.GetGazeState(actor).Mode.ToString() });
            }
            case "/gaze":
            {
                var g = _gaze.GetGazeState(actor);
                return Json(new { mode = g.Mode.ToString() });
            }
            case "/equip":
            {
                unsafe
                {
                    var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
                    if (character == null)
                        return Json(new { error = "no character" });
                    var equips = new List<string>();
                    var ids = character->DrawData.EquipmentModelIds;
                    for (int i = 0; i < ids.Length; i++)
                        equips.Add($"{ids[i].Id}.{ids[i].Variant}.{ids[i].Stain0}");
                    var main = character->DrawData.Weapon(FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot.MainHand).ModelId;
                    var cust = new System.ReadOnlySpan<byte>((byte*)&character->DrawData.CustomizeData, 26);
                    string? humanEquip = null, humanCust = null, raceSex = null;
                    var draw = (byte*)character->GameObject.DrawObject;
                    if (draw != null)
                    {
                        var he = new List<string>();
                        for (int i = 0; i < 10; i++)
                        {
                            byte* e = draw + 0xA40 + i * 4;
                            he.Add($"{*(ushort*)e}.{e[2]}.{e[3]}");
                        }
                        humanEquip = string.Join(" ", he);
                        humanCust = Convert.ToHexString(new System.ReadOnlySpan<byte>(draw + 0xA20, 26));
                        raceSex = $"{*(ushort*)(draw + 0xAA0):X4}";
                    }
                    var glassesIds = character->DrawData.GlassesIds;
                    string flags = $"{*((byte*)&character->DrawData + 0x23E):X2}{*((byte*)&character->DrawData + 0x23F):X2}";
                    return Json(new
                    {
                        equipment = string.Join(" ", equips),
                        main = $"{main.Id}.{main.Type}.{main.Variant}",
                        customize = Convert.ToHexString(cust),
                        humanEquip, humanCust, raceSex,
                        glasses = $"{glassesIds[0]} {glassesIds[1]}",
                        flags,
                        modelCharaId = character->ModelContainer.ModelCharaId,
                        skeletonId = character->ModelContainer.ModelSkeletonId,
                        drawObject = $"0x{(nint)draw:X}",
                    });
                }
            }
            case "/meta":
            {
                var meta = _integration.GetActorMetaManipulations(id);
                if (!meta.Success || meta.Value is not { } m)
                    return Json(new { error = meta.Detail });
                return Json(new { length = m.Length, hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(m)))[..12] });
            }
            case "/spawncatalog":
            {
                string want = query.TryGetValue("name", out var cn) ? cn.ToLowerInvariant() : "wind-up titan";
                global::Poser.Services.SpawnCatalogEntry? entry = null;
                foreach (var e in _catalog.Entries)
                    if (e.NameLower == want || (entry == null && e.NameLower.Contains(want)))
                        entry = e;
                if (entry is not { } found)
                    return Json(new { ok = false, detail = "no catalog entry matches" });
                var spawnedActor = _lifecycle.SpawnActor($"Add {found.Name}", () => _spawner.SpawnCatalogActor(found));
                return Json(new { ok = spawnedActor != null, name = spawnedActor?.Name, entry = found.Name, kind = found.Kind.ToString() });
            }
            case "/spawnobject":
            {
                string modelPath = query.TryGetValue("path", out var sp) ? sp : "bgcommon/hou/outdoor/general/0022/bgparts/gar_b0_m0022a.mdl";
                var seat = _worldObjects.Adopted.Count > 0 ? _worldObjects.Adopted[0].Transform : global::Poser.Transform.Identity;
                var placement = new global::Poser.Transform(seat.Position + new System.Numerics.Vector3(0f, 0.5f, 0f), seat.Rotation, System.Numerics.Vector3.One);
                var made = _worldObjects.Spawn(modelPath, placement, true, out var detail);
                return Json(new { ok = made != null, detail, address = made == null ? null : $"0x{made.Address:X}", name = made?.Name });
            }
            case "/worldobjects":
            {
                var rows = new List<object>();
                unsafe
                {
                    foreach (var handle in _worldObjects.Adopted)
                    {
                        var node = (byte*)handle.Address;
                        string tail = node == null ? "" : Convert.ToHexString(new System.ReadOnlySpan<byte>(node + 0xC0, 0x20));
                        rows.Add(new { handle.Name, handle.Spawned, handle.IsVfx, address = $"0x{handle.Address:X}", paused = handle.AnimationPaused, ready = _worldObjects.IsReadyProbe(handle), tail });
                    }
                }
                return Json(new { rows });
            }
            case "/redraw":
            {
                var r = _integration.RequestRedraw(id);
                return Json(new { ok = r.Success, r.Detail });
            }
            case "/collections":
            {
                var list = _session.ListCollections();
                return Json(new { ok = list.Success, list.Detail, items = list.Value?.Select(i => $"{i.Name} {i.Id}").ToArray() });
            }
            case "/clips":
                _port.ProbeClips(id);
                return Json(new { ok = true });
            case "/clone":
            {
                var clone = _lifecycle.SpawnActor($"Bridge clone of {actor.Name}", () => _spawner.CloneActor(actor));
                return Json(new { ok = clone != null, name = clone?.Name, id = clone != null ? _bindings.GetActorId(clone)?.ToString() : null });
            }
            case "/dupepose":
            case "/dupe":
            {
                bool posed = path == "/dupepose";
                IActor? Wearing()
                {
                    var c = _spawner.CloneActor(actor);
                    if (c != null && _bindings.GetActorId(c) is { } cid)
                        _lifecycle.WhenPosable(c, copy =>
                        {
                            _spawner.CopyDrawnAppearance(actor, (IActor)copy);
                            _spawner.CopyEquipmentVisibility(actor, (IActor)copy);
                        });
                    return c;
                }
                var copy = posed
                    ? _lifecycle.SpawnActorWithPose($"Duplicate actor '{actor.Name}' with pose", Wearing, actor)
                    : _lifecycle.SpawnActor($"Duplicate actor '{actor.Name}'", Wearing);
                var copyId = copy != null ? _bindings.GetActorId(copy) : null;
                if (posed && copyId is { } pid)
                {
                    _animation.Pause(pid);
                    _gaze.SetGazeMode(copy!, GazeTargetMode.Detached);
                }
                return Json(new { ok = copy != null, name = copy?.Name, id = copyId?.ToString() });
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
            renderFlags = snapshot?.RenderFlags,
            hasDrawObject = snapshot?.HasDrawObject,
            physicsFrozen = _port.IsPhysicsFrozen,
            drawObject = DrawObjectAddress(actor),
            drawObjectVisible = snapshot?.DrawObjectVisible,
            weaponDrawn = snapshot?.WeaponDrawn,
            weaponHidden = snapshot?.WeaponHidden,
            hatHidden = snapshot?.HatHidden,
            visorToggled = snapshot?.VisorToggled,
            bodyProfile = BodyProfile(id),
            integration = Owned(id),
            collection = _session.ReadCollection(id) is { Success: true, Value: { } c } ? $"{c.EffectiveName} {c.EffectiveId} individual={c.HasIndividualAssignment}" : null,
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
    private static extern unsafe bool ReadProcessMemory(
        nint process, nint address, void* buffer, nint size, out nint read);

    /// <summary>Writes one float (f=) or int (i=) at addr — a probe tool.</summary>
    private static unsafe string Poke(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("addr", out var a))
            return Json(new { error = "addr (hex) is required" });
        nint address = (nint)Convert.ToInt64(a.Replace("0x", string.Empty), 16);
        var probe = new byte[4];
        fixed (byte* dst = probe)
        {
            if (!ReadProcessMemory((nint)(-1), address, dst, 4, out var read) || read != 4)
                return Json(new { error = "unreadable", address = $"0x{address:X}" });
        }
        if (query.TryGetValue("f", out var f))
            *(float*)address = float.Parse(f, CultureInfo.InvariantCulture);
        else if (query.TryGetValue("i", out var i))
            *(int*)address = int.Parse(i, CultureInfo.InvariantCulture);
        else
            return Json(new { error = "f= or i= is required" });
        return Json(new { ok = true, address = $"0x{address:X}" });
    }

    /// <summary>Guarded read of any address: floats and ints per dword.</summary>
    private static unsafe string Peek(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("addr", out var a))
            return Json(new { error = "addr (hex) is required" });
        nint address = (nint)Convert.ToInt64(a.Replace("0x", string.Empty), 16);
        int size = query.TryGetValue("n", out var n)
            && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Min(v, 0x800) : 0x40;
        var buffer = new byte[size];
        bool ok;
        nint read;
        fixed (byte* dst = buffer)
            ok = ReadProcessMemory((nint)(-1), address, dst, size, out read) && read == size;
        if (!ok)
            return Json(new { error = "unreadable", address = $"0x{address:X}" });
        var words = new List<object>();
        for (int off = 0; off + 4 <= size; off += 4)
        {
            float f = BitConverter.ToSingle(buffer, off);
            words.Add(new { off = $"{off:x3}", f = float.IsFinite(f) ? f : (float?)null, i = BitConverter.ToInt32(buffer, off) });
        }
        return Json(new { address = $"0x{address:X}", words });
    }

    private object Owned(ActorId id)
    {
        var o = _session.OverridesFor(id);
        return new { o.CollectionOwned, o.CollectionName, o.DesignOwned, tempBody = o.TemporaryBodyProfile?.ToString(), bodyJson = o.BodyProfileJson?.Length, mcdf = o.Mcdf?.FileName, mcdfBody = o.Mcdf?.AppliedProfileJson?.Length, mcdfCollection = o.Mcdf?.TemporaryCollection?.ToString() };
    }

    private static unsafe string DrawObjectAddress(IActor actor)
    {
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor.Address;
        return character == null ? "0" : $"0x{(nint)character->GameObject.DrawObject:X}";
    }

    private object BodyProfile(ActorId id)
    {
        var probe = _integration.ProbeBodyProfile(id);
        return new
        {
            ok = probe.Success,
            detail = probe.Detail,
            active = probe.Value?.ActiveProfile?.ToString(),
            saved = probe.Value?.ActiveIsSaved,
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
