#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Poser.Application.Posing;
using Poser.Application.Presentation;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Game.Presentation;

namespace Poser.Bridge;

/// <summary>
/// Bridge commands for inspector controls that check scripts need: Reset → All, the
/// whole actor's animation speed, Lock position, the face sliders, the actor's
/// presentation values and a light's properties. Each one calls the same command
/// as the control, and each one only reads when it is given nothing to change.
/// </summary>
public sealed partial class DebugBridge
{
    /// <summary>Reset → All in the inspector's Pose section.</summary>
    private string ResetAll(ActorId id)
    {
        var result = ((IActorResetControl)_services.GetService(typeof(IActorResetControl))!).ResetAll(id);
        return Json(new { ok = result.Success, result.Detail, history = History() });
    }

    /// <summary>The Speed slider for the whole actor (value=SPEED), or clear=1 to give
    /// speed back to the game.</summary>
    private string ActorSpeed(Dictionary<string, string> query, ActorId id)
    {
        Outcome? result = null;
        if (query.TryGetValue("value", out var value))
            result = _animation.SetSpeed(id, float.Parse(value, CultureInfo.InvariantCulture));
        else if (query.GetValueOrDefault("clear") == "1")
            result = _animation.ClearSpeed(id);
        return Json(new { ok = result?.Success, result?.Detail,
            speed = _animation.OverridesFor(id).OverallSpeed, gameSpeed = _animation.Read(id)?.OverallSpeed,
            paused = _animation.IsPaused(id) });
    }

    /// <summary>The Lock position switch in the Animation tab (on=1|0).</summary>
    private string PositionLock(Dictionary<string, string> query, ActorId id)
    {
        Outcome? result = null;
        if (query.TryGetValue("on", out var on))
            result = _animation.SetPositionLock(id, on == "1");
        return Json(new { ok = result?.Success, result?.Detail, locked = _animation.OverridesFor(id).PositionLock });
    }

    /// <summary>The face sliders in the inspector's Expression section: unit=ID&amp;weight=W
    /// sets one slider, reset=1 resets them all. Reads every slider either way.</summary>
    private string FaceSliders(Dictionary<string, string> query, ActorId id)
    {
        var face = (IExpressionControl)_services.GetService(typeof(IExpressionControl))!;
        Outcome? result = null;
        if (query.TryGetValue("unit", out var unit))
            result = face.SetWeight(id, unit,
                float.Parse(query.GetValueOrDefault("weight", "0"), CultureInfo.InvariantCulture));
        else if (query.GetValueOrDefault("reset") == "1")
            result = face.Reset(id);
        if (result != null)
            face.Seal();
        return Json(new
        {
            ok = result?.Success, result?.Detail,
            available = face.IsAvailable && face.HasActor(id),
            active = face.HasActiveExpression(id),
            units = face.GetUnits(id).Select(u => new { id = u.Id, label = u.Label, weight = face.GetWeight(id, u.Id) }),
            history = History(),
        });
    }

    /// <summary>The Appearance tab's visibility, opacity, tint and wetness controls:
    /// visible=1|0, opacity=0..1, tint=R,G,B,A with model=Character|MainHand|OffHand,
    /// wet=1|0 (hold the wetness or let the game set it), wetness=WEATHER,SWIMMING,DEPTH
    /// (the wetness sliders; they need the wetness held, by wet=1 in the same request or an
    /// earlier one), reset=1.</summary>
    private string Presentation(Dictionary<string, string> query, ActorId id)
    {
        var values = (IActorValueControl)_services.GetService(typeof(IActorValueControl))!;
        var results = new List<Outcome>();
        if (query.TryGetValue("visible", out var visible))
            results.Add(values.SetVisibility(id, visible == "1"));
        if (query.TryGetValue("opacity", out var opacity))
            results.Add(values.SetOpacity(id, float.Parse(opacity, CultureInfo.InvariantCulture)));
        if (query.TryGetValue("tint", out var tint))
        {
            var parts = tint.Split(',').Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var model = Enum.Parse<PresentationModel>(query.GetValueOrDefault("model", "Character"), true);
            results.Add(values.SetTint(id, model, new Vector4(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : 1f)));
        }
        if (query.TryGetValue("wet", out var wet))
            results.Add(values.SetWetnessEnabled(id, wet == "1"));
        if (query.TryGetValue("wetness", out var wetness))
        {
            var parts = wetness.Split(',').Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            results.Add(values.SetWetness(id, new WetnessState(parts[0], parts[1], parts[2])));
        }
        if (query.GetValueOrDefault("reset") == "1")
            results.Add(values.ResetPresentation(id));
        if (results.Count > 0)
            values.Seal();
        var presentation = (ActorPresentationSession)_services.GetService(typeof(ActorPresentationSession))!;
        var reading = presentation.Read(id);
        var owned = presentation.OverridesFor(id);
        static float[]? Color(Vector4? c) => c is { } v ? new[] { v.X, v.Y, v.Z, v.W } : null;
        return Json(new
        {
            ok = results.Count == 0 ? (bool?)null : results.All(r => r.Success),
            detail = results.FirstOrDefault(r => !r.Success).Detail,
            visible = values.ReadVisibility(id),
            opacity = reading?.Opacity,
            tint = Color(reading?.CharacterTint),
            wet = owned.Wetness != null,
            wetness = reading is { } r ? new { r.Wetness.Weather, r.Wetness.Swimming, r.Wetness.Depth } : null,
            ownedOpacity = owned.Opacity,
            history = History(),
        });
    }

    /// <summary>The Light tab's controls for one light (light=INDEX or its selection id):
    /// property=NAME&amp;value=VALUE sets a property from LightProperties. A colour or
    /// angle pair is written as numbers separated by commas.</summary>
    private string LightCommand(Dictionary<string, string> query)
    {
        var lights = _sceneSession.Snapshot.Lights;
        var key = query.GetValueOrDefault("light", "0");
        var light = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < lights.Count ? lights[index]
            : lights.FirstOrDefault(l => SelectionId.ForLight(l.Id).ToString() == key);
        if (light == null)
            return Json(new { error = "No such light.", count = lights.Count });
        var control = (ILightControl)_services.GetService(typeof(ILightControl))!;
        Outcome? result = null;
        if (query.TryGetValue("property", out var name))
        {
            var field = typeof(LightProperties).GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (field?.GetValue(null) is not EntityProperty property || field.FieldType.GetGenericArguments() is not [_, var type])
                return Json(new { error = $"No light property is called {name}." });
            var text = query.GetValueOrDefault("value", "");
            float[] Numbers() => text.Split(',').Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            object parsed = type == typeof(bool) ? text is "1" or "true"
                : type == typeof(float) ? float.Parse(text, CultureInfo.InvariantCulture)
                : type == typeof(Vector3) ? new Vector3(Numbers()[0], Numbers()[1], Numbers()[2])
                : type == typeof(Vector2) ? new Vector2(Numbers()[0], Numbers()[1])
                : type.IsEnum ? Enum.Parse(type, text, true)
                : text;
            var set = typeof(ILightControl).GetMethod(nameof(ILightControl.Set))!.MakeGenericMethod(type);
            result = (Outcome)set.Invoke(control, new[] { light.Id, property, parsed })!;
            control.Seal();
        }
        return JsonSerializer.Serialize(new
        {
            ok = result?.Success, result?.Detail, id = SelectionId.ForLight(light.Id).ToString(),
            light = control.Read(light.Id), history = History(),
        }, new JsonSerializerOptions { IncludeFields = true });
    }
}
#endif
