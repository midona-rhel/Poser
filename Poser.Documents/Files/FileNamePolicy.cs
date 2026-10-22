namespace Poser.Documents.Files;

/// <summary>Shared filename character rules; callers retain collision and extension policy.</summary>
public static class FileNamePolicy
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static bool HasInvalidCharacters(string name) => name.IndexOfAny(Invalid) >= 0;

    public static string FilterInput(string name) => Rewrite(name, null);

    public static string GeneratedStem(string name, string fallback)
    {
        var cleaned = Rewrite(name, '_').Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string Rewrite(string name, char? replacement)
    {
        if (!HasInvalidCharacters(name)) return name;
        var chars = new char[name.Length];
        int count = 0;
        foreach (var ch in name)
        {
            if (Array.IndexOf(Invalid, ch) < 0) chars[count++] = ch;
            else if (replacement is { } value) chars[count++] = value;
        }
        return new string(chars, 0, count);
    }
}
