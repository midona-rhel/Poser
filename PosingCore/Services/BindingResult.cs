namespace Poser.Services;

public enum BindingStatus
{
    Success,
    StaleTarget,
    IdentityMismatch,
    Missing,
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
