namespace Poser.Domain;

/// <summary>
/// The one plain result of a command at any layer: success, or a refusal
/// whose detail is the text a surface shows. A refusal passes up through
/// sessions and ports unchanged, so it is worded once where it arises.
/// Results that carry more (a recovery, a receipt, an appearance refusal)
/// keep their own types.
///
/// A refusal is permanent unless its source knows it will clear by itself
/// or by a user action outside history (an active transform gesture, an
/// appearance hold the user releases): only those are <see cref="Transient"/>.
/// History retries a transient refusal in place; a permanent one is
/// reported, then skipped when it repeats.
/// </summary>
public readonly record struct Outcome(bool Success, string? Detail = null)
{
    /// <summary>The refusal is temporary; retrying the same write later can land.</summary>
    public bool Transient { get; init; }

    public static Outcome Ok() => new(true);
    public static Outcome Fail(string detail) => new(false, detail);

    /// <summary>A temporary refusal: a history step stays at the cursor for retry.</summary>
    public static Outcome Busy(string? detail) => new(false, detail) { Transient = true };
}
