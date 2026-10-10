namespace Poser.Game.Services;

public enum BindingStatus
{
    Success,
    StaleTarget,
    IdentityMismatch,
    Missing,
    /// <summary>Asked off the framework thread; nothing was resolved.</summary>
    WrongThread,
}

/// <summary>What a stable id resolves to right now: the live entity, or
/// why there is none.</summary>
public readonly record struct BindingResult<T>(
    BindingStatus Status,
    T? Value = default,
    string? Detail = null)
    where T : class
{
    public bool Success => Status == BindingStatus.Success && Value != null;
}
