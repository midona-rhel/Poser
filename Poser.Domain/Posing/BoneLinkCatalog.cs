namespace Poser.Domain.Posing;

/// <summary>Canonical same-delta bone groups.</summary>
public static class BoneLinkCatalog
{
    private static readonly string[][] Sets =
    {
        ["j_f_eye_r", "j_f_eye_l"],
        ["j_zera_a_l", "j_zerb_a_l", "j_zerc_a_l", "j_zerd_a_l"],
        ["j_zera_a_r", "j_zerb_a_r", "j_zerc_a_r", "j_zerd_a_r"],
        ["j_zera_b_l", "j_zerb_b_l", "j_zerc_b_l", "j_zerd_b_l"],
        ["j_zera_b_r", "j_zerb_b_r", "j_zerc_b_r", "j_zerd_b_r"],
    };

    private static readonly IReadOnlyDictionary<string, string[]> Lookup =
        Sets.SelectMany(set => set.Select(name => (
                name,
                linked: set.Where(candidate => candidate != name).ToArray())))
            .ToDictionary(pair => pair.name, pair => pair.linked);

    public static IReadOnlyList<string> GetLinked(string canonicalName) =>
        Lookup.TryGetValue(canonicalName, out var linked)
            ? linked
            : Array.Empty<string>();
}
