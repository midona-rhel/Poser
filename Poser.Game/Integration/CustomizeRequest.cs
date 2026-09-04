using System.Globalization;
using Newtonsoft.Json.Linq;
using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>Builds a temporary customize-only design without changing the captured state.</summary>
internal static class CustomizeRequest
{
    internal static IntegrationValue<JObject> Build(JObject snapshot, IReadOnlyDictionary<CustomizeKey, int> values)
    {
        if (!ByteValue(snapshot["FileVersion"], out int version) || version != 1)
            return IntegrationValue<JObject>.Fail("Glamourer's state has an unsupported file version.");
        if (snapshot["Customize"] is not JObject original)
            return IntegrationValue<JObject>.Fail("The state carries no customization.");
        if (original["ModelId"] is { } model && (!ByteValue(model, out int modelId) || modelId != 0))
            return IntegrationValue<JObject>.Fail("Customization requires a human actor model.");

        foreach (var key in values.Keys.Concat(new[] { CustomizeKey.Race, CustomizeKey.Clan, CustomizeKey.Gender, CustomizeKey.BodyType }).Distinct())
        {
            if (!Enum.IsDefined(key) || original[key.ToString()] is not JObject field
                || (key == CustomizeKey.Wetness ? field["Value"]?.Type != JTokenType.Boolean : !ByteValue(field["Value"], out _))
                || field["Apply"] is { Type: not JTokenType.Boolean })
                return IntegrationValue<JObject>.Fail($"Glamourer's {key} customization field is missing or malformed.");
        }
        foreach (var (key, value) in values)
            if (value < 0 || value > (key == CustomizeKey.Wetness ? 1 : byte.MaxValue))
                return IntegrationValue<JObject>.Fail($"The requested {key} value is out of range.");

        var request = (JObject)snapshot.DeepClone();
        // DesignBase has these sections only. A state is not a saved design:
        // do not carry links or future application metadata into this edit.
        foreach (var property in request.Properties().ToArray())
            if (property.Name is not ("FileVersion" or "Customize" or "Equipment" or "Bonus" or "Parameters"))
                property.Remove();
        DisableApplication(request);
        var customize = (JObject)request["Customize"]!;
        foreach (var (key, value) in values)
        {
            var field = (JObject)customize[key.ToString()]!;
            field["Value"] = key == CustomizeKey.Wetness ? new JValue(value != 0) : new JValue(value);
            field["Apply"] = true;
        }
        int race = customize["Race"]!["Value"]!.Value<int>();
        int clan = customize["Clan"]!["Value"]!.Value<int>();
        int gender = customize["Gender"]!["Value"]!.Value<int>();
        if (race == 0 || clan == 0 || (clan + 1) / 2 != race || gender > 1)
            return IntegrationValue<JObject>.Fail("The requested race, clan and gender do not form a valid body. Change race and clan together.");
        // Glamourer's parser always applies nonzero BodyType; Clan also
        // implies Race in StateEditor. Make these structural bits explicit.
        customize["BodyType"]!["Apply"] = customize["BodyType"]!["Value"]!.Value<int>() != 0;
        if (values.ContainsKey(CustomizeKey.Clan))
            customize["Race"]!["Apply"] = true;
        return IntegrationValue<JObject>.Ok(request);
    }

    private static bool ByteValue(JToken? token, out int value)
    {
        value = 0;
        return token?.Type == JTokenType.Integer
            && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value is >= 0 and <= byte.MaxValue;
    }

    private static void DisableApplication(JToken token)
    {
        if (token is JObject obj)
            foreach (var property in obj.Properties())
            {
                if (property.Name.StartsWith("Apply", StringComparison.Ordinal))
                    property.Value = false;
                else
                    DisableApplication(property.Value);
            }
        else if (token is JArray array)
            foreach (var child in array)
                DisableApplication(child);
    }
}
