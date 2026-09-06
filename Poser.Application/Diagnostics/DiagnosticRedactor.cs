using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Poser.Application.Diagnostics;

/// <summary>One report-session policy for decoded JSON strings and free-text diagnostics.</summary>
public sealed class DiagnosticRedactor
{
    private static readonly Regex AbsolutePath = new(
        "(?<![\\w:])(?:[A-Za-z]:[\\\\/]|[\\\\/]{2}(?![\\\\/]))[^\\r\\n\\t<>|\":;]*",
        RegexOptions.CultureInvariant);
    private static readonly Regex Extension = new(@"\.[A-Za-z0-9]{1,10}$", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, string> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathParts = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterIdentity(string name, string token)
    {
        if (!string.IsNullOrWhiteSpace(name) && !name.Equals(token, StringComparison.OrdinalIgnoreCase))
            _identities[name] = token;
    }

    public void RegisterPathsJson(string json) => VisitStrings(JsonNode.Parse(json), RememberPaths);

    private void RememberPaths(string text)
    {
        foreach (Match match in AbsolutePath.Matches(text))
            foreach (string part in match.Value.TrimEnd(' ', '.', ')', ']', ',', '\'').Split(['\\', '/']))
                if (part.Length > 2)
                    _pathParts.Add(part);
    }

    public string ScrubText(string text)
    {
        RememberPaths(text);
        text = AbsolutePath.Replace(text, match =>
        {
            string path = match.Value.TrimEnd(' ', '.', ')', ']', ',', '\'');
            string key = path.Replace('\\', '/');
            if (!_paths.TryGetValue(key, out string? token))
                _paths[key] = token = $"[path {_paths.Count + 1}]";
            return token + Extension.Match(path).Value + match.Value[path.Length..];
        });
        foreach (var pair in _identities.OrderByDescending(pair => pair.Key.Length))
            text = text.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        foreach (string part in _pathParts.OrderByDescending(part => part.Length))
            text = Regex.Replace(text, @"(?<!\w)" + Regex.Escape(part) + @"(?!\w)",
                "[private]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    public string ScrubJson(string json)
    {
        var root = JsonNode.Parse(json);
        // Discover first, so a root mentioned late in settings also sanitizes
        // earlier exception messages containing only its host or private folder.
        VisitStrings(root, RememberPaths);
        return Rewrite(root)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private JsonNode? Rewrite(JsonNode? node) => node switch
    {
        JsonObject obj => RewriteObject(obj),
        JsonArray array => new JsonArray(array.Select(Rewrite).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(ScrubText(text)),
        _ => node?.DeepClone(),
    };

    private JsonObject RewriteObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var pair in obj)
        {
            string key = ScrubText(pair.Key);
            // Redacted dictionary names can collide; keep every diagnostic value.
            string unique = key;
            for (int suffix = 2; result.ContainsKey(unique); suffix++) unique = $"{key} {suffix}";
            result[unique] = Rewrite(pair.Value);
        }
        return result;
    }

    private static void VisitStrings(JsonNode? node, Action<string> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) { visit(pair.Key); VisitStrings(pair.Value, visit); }
                break;
            case JsonArray array:
                foreach (var child in array) VisitStrings(child, visit);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                visit(text);
                break;
        }
    }
}
