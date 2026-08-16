using System;

namespace Poser.Config;

/// <summary>One upstream project Poser is derivative of, and the repository
/// the attribution links to.</summary>
public readonly record struct UpstreamProject(
    string Name,
    string Url,
    string Credit);

/// <summary>
/// The first-run notice's data and its two decisions: whether a stored config
/// has already accepted the notice the CURRENT build ships, and whether typed
/// text confirms it.
///
/// <para>Acceptance is stored as a VERSION, not a flag
/// (<see cref="PoserConfiguration.AcceptedNoticeVersion"/>): revising the
/// notice means bumping <see cref="CurrentVersion"/>, and every config that
/// accepted an older revision is prompted once more. A config that never saw
/// the notice carries 0, which is below every revision.</para>
/// </summary>
public static class FirstRunNotice
{
    /// <summary>The revision of the notice text this build shows. Bump it when
    /// the notice says something materially new; nothing else re-prompts.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>What the user types to confirm. Compared trimmed and
    /// case-insensitively, and NOT otherwise normalised — the dialog quotes
    /// this string, so what it asks for is what it accepts.</summary>
    public const string ConfirmationPhrase = "I accept";

    /// <summary>The projects Poser is derivative of. One list, read by the
    /// notice dialog and by Settings → About, so the two surfaces cannot
    /// drift. Credits are the names those projects publish for themselves
    /// (plugin manifests and README credit sections).</summary>
    public static readonly UpstreamProject[] Upstream =
    [
        new(
            "Anamnesis",
            "https://github.com/imchillin/Anamnesis",
            "ergoxiv and chirpxiv, after Yuki, Luminiari, Peebs-miqo and AsgardXIV"),
        new(
            "Ktisis",
            "https://github.com/ktisis-tools/Ktisis",
            "Chirp, Cazzar, Bwuny and contributors"),
        new(
            "Brio",
            "https://github.com/Etheirys/Brio",
            "Minmoose, Asgard and contributors"),
    ];

    /// <summary>True once the stored config has accepted a notice at least as
    /// new as this build's.</summary>
    public static bool IsAccepted(PoserConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AcceptedNoticeVersion >= CurrentVersion;
    }

    /// <summary>The gate predicate: trimmed, case-insensitive equality with
    /// <see cref="ConfirmationPhrase"/>. Leading and trailing whitespace is
    /// forgiven; interior whitespace is not, because the phrase is quoted to
    /// the user verbatim.</summary>
    public static bool Confirms(string? typed) =>
        typed is not null
        && string.Equals(
            typed.Trim(),
            ConfirmationPhrase,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Records acceptance of THIS build's notice. Persisting the
    /// config is the caller's, so the write and the save stay one act at the
    /// call site.</summary>
    public static void Accept(PoserConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.AcceptedNoticeVersion = CurrentVersion;
    }
}
