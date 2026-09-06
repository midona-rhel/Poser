using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Journal;

namespace Poser.Game.Integration;

public sealed class CharaImport(ActorIntegrationSession integration, DisruptiveSteps history)
{
    public IntegrationResult Apply(ActorId actor, string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4 * 1024 * 1024)
                return new(false, "The character file exceeds 4 MiB.");
            using var text = new StreamReader(stream);
            using var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
            var file = JObject.Load(reader);
            if (reader.Read()) return new(false, "The character file has trailing data.");
            var before = integration.GetStateJson(actor);
            if (!before.Success || before.Value is null)
                return new(false, before.Detail, before.AppearanceRefusal);
            var request = CharaRequest.Build(JObject.Parse(before.Value), file);
            if (!request.Success || request.Value is null)
                return new(false, request.Detail);
            var own = integration.OwnLook(actor);
            if (!own.Success) return own;
            string after = request.Value.ToString(Formatting.None);
            return history.Run(actor, "Import character appearance",
                () => integration.ApplyStateJson(actor, after),
                () => integration.ApplyStateJson(actor, before.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(false, $"The character file could not be read: {ex.Message}");
        }
    }
}

internal static class CharaRequest
{
    // Anamnesis/Brio store packed native bytes; Glamourer's customization
    // fields split the high-bit switches and facial-feature mask apart.
    private static readonly (string File, CustomizeKey Key)[] Fields =
    [
        ("Height", CustomizeKey.Height), ("Head", CustomizeKey.Face), ("Hair", CustomizeKey.Hairstyle),
        ("Skintone", CustomizeKey.SkinColor), ("REyeColor", CustomizeKey.EyeColorRight),
        ("HairTone", CustomizeKey.HairColor), ("Highlights", CustomizeKey.HighlightsColor),
        ("LimbalEyes", CustomizeKey.TattooColor), ("Eyebrows", CustomizeKey.Eyebrows),
        ("LEyeColor", CustomizeKey.EyeColorLeft), ("Nose", CustomizeKey.Nose), ("Jaw", CustomizeKey.Jaw),
        ("LipsToneFurPattern", CustomizeKey.LipColor), ("EarMuscleTailSize", CustomizeKey.MuscleMass),
        ("TailEarsType", CustomizeKey.TailShape), ("Bust", CustomizeKey.BustSize),
        ("FacePaintColor", CustomizeKey.FacePaintColor),
    ];
    private static readonly (string File, string Slot)[] Gear =
    [
        ("MainHand", "MainHand"), ("OffHand", "OffHand"), ("HeadGear", "Head"), ("Body", "Body"),
        ("Hands", "Hands"), ("Legs", "Legs"), ("Feet", "Feet"), ("Ears", "Ears"),
        ("Neck", "Neck"), ("Wrists", "Wrists"), ("LeftRing", "LFinger"), ("RightRing", "RFinger"),
    ];

    internal static IntegrationValue<JObject> Build(JObject snapshot, JObject file)
    {
        try
        {
            var values = new Dictionary<CustomizeKey, int>();
            foreach (var (name, key) in Fields)
                if (Present(file, name)) values[key] = Number(file[name], 255);
            Named("Race", CustomizeKey.Race, ["Hyur", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera"], 1);
            Named("Tribe", CustomizeKey.Clan, ["Midlander", "Highlander", "Wildwood", "Duskwight", "Plainsfolk", "Dunesfolk", "SeekerOfTheSun", "KeeperOfTheMoon", "SeaWolf", "Hellsguard", "Raen", "Xaela", "Helions", "TheLost", "Rava", "Veena"], 1);
            Named("Gender", CustomizeKey.Gender, ["Masculine", "Feminine"], 0);
            if (Present(file, "Age"))
                values[CustomizeKey.BodyType] = file["Age"]!.Type == JTokenType.String
                    ? (string?)file["Age"] switch { "Normal" => 1, "Old" => 3, "Young" => 4, _ => throw new JsonException("Unknown body type.") }
                    : Number(file["Age"], 255);
            if (Present(file, "EnableHighlights"))
            {
                if (file["EnableHighlights"]!.Type != JTokenType.Boolean) throw new JsonException("EnableHighlights must be boolean.");
                values[CustomizeKey.Highlights] = file.Value<bool>("EnableHighlights") ? 1 : 0;
            }
            Split("Eyes", CustomizeKey.EyeShape, CustomizeKey.SmallIris);
            Split("Mouth", CustomizeKey.Mouth, CustomizeKey.Lipstick);
            Split("FacePaint", CustomizeKey.FacePaint, CustomizeKey.FacePaintReversed);
            if (Present(file, "FacialFeatures"))
            {
                int mask = file["FacialFeatures"]!.Type == JTokenType.String
                    ? FeatureMask(file.Value<string>("FacialFeatures")!) : Number(file["FacialFeatures"], 255);
                for (int i = 0; i < 8; i++) values[CustomizeKey.FacialFeature1 + i] = (mask >> i) & 1;
            }
            if (Present(file, "ModelType") && Number(file["ModelType"], int.MaxValue) != 0)
                return IntegrationValue<JObject>.Fail("This character file uses a nonhuman model; .chara import currently supports human appearance.");
            if (values.Count == 0 && !Gear.Any(g => Present(file, g.File)) && !Present(file, "Glasses"))
                return IntegrationValue<JObject>.Fail("The file contains no supported character appearance.");
            var result = CustomizeRequest.Build(snapshot, values);
            if (!result.Success || result.Value is null) return result;
            var request = result.Value;
            if (request["Equipment"] is not JObject) request["Equipment"] = new JObject();
            var equipment = (JObject)request["Equipment"]!;
            foreach (var (name, slot) in Gear)
            {
                if (!Present(file, name)) continue;
                if (file[name] is not JObject item) throw new JsonException($"{name} must be an item.");
                bool weapon = name is "MainHand" or "OffHand";
                ushort model = (ushort)Number(item[weapon ? "ModelSet" : "ModelBase"], ushort.MaxValue);
                ushort type = weapon ? (ushort)Number(item["ModelBase"], ushort.MaxValue) : (ushort)0;
                byte variant = (byte)Number(item["ModelVariant"], byte.MaxValue);
                equipment[slot] = new JObject
                {
                    ["ItemId"] = model == 0 ? 0ul : WardrobeIds.Custom(model, type, variant),
                    ["Stain"] = Number(item["DyeId"] ?? new JValue(0), 255),
                    ["Stain2"] = Number(item["DyeId2"] ?? new JValue(0), 255),
                    ["Apply"] = true, ["ApplyStain"] = true, ["ApplyStain2"] = true,
                };
            }
            if (Present(file, "Glasses"))
            {
                ushort glasses = (ushort)Number(file["Glasses"]?["GlassesId"], ushort.MaxValue);
                // GlassesId is a sheet row (not its draw model); Penumbra's
                // CustomItemId marks bonus rows with bit 49, without bit 48.
                request["Bonus"] = new JObject { ["Glasses"] = new JObject { ["BonusId"] = glasses | (1ul << 49), ["Apply"] = true } };
            }
            return IntegrationValue<JObject>.Ok(request);

            void Named(string name, CustomizeKey key, string[] names, int first)
            {
                if (!Present(file, name)) return;
                if (file[name]!.Type != JTokenType.String) { values[key] = Number(file[name], 255); return; }
                string value = file.Value<string>(name)!;
                if (value == "Lalafel") value = "Lalafell"; // Brio's spelling.
                int index = Array.FindIndex(names, n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new JsonException($"Unknown {name}: {value}.");
                values[key] = index + first;
            }
            void Split(string name, CustomizeKey key, CustomizeKey flag)
            {
                if (!Present(file, name)) return;
                int value = Number(file[name], 255);
                values[key] = value & 0x7f;
                values[flag] = value >> 7;
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return IntegrationValue<JObject>.Fail($"Invalid character appearance: {ex.Message}");
        }
    }

    private static bool Present(JObject file, string name) => file[name] is { Type: not JTokenType.Null };
    private static int Number(JToken? token, int max) => token?.Type == JTokenType.Integer
        && long.TryParse(token.ToString(), out long value) && value >= 0 && value <= max
            ? (int)value : throw new JsonException($"Expected a number between 0 and {max}.");
    private static int FeatureMask(string text)
    {
        string[] names = ["First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "LegacyTattoo"];
        int mask = 0;
        foreach (string value in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (value == "None") continue;
            int i = Array.IndexOf(names, value);
            if (i < 0) throw new JsonException($"Unknown facial feature: {value}.");
            mask |= 1 << i;
        }
        return mask;
    }
}
