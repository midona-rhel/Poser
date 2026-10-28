namespace Poser.Domain;

/// <summary>
/// A command's success or failure, with an optional explanation for the user.
/// Transient marks a temporary failure that callers may retry. A success
/// covers the command's stated work; it does not imply later asynchronous
/// work has finished.
/// </summary>
public readonly record struct Outcome(bool Success, string? Detail = null)
{
    /// <summary>The failure may clear without changing the requested operation.</summary>
    public bool Transient { get; init; }

    public static Outcome Ok() => new(true);
    public static Outcome Fail(string detail) => new(false, detail);

    /// <summary>Reports a temporary failure with an optional explanation.</summary>
    public static Outcome Busy(string? detail) => new(false, detail) { Transient = true };
}
