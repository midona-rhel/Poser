using Poser.Domain.Transforms;

namespace Poser.UI;

/// <summary>The actor transform the inspector's Copy last took. One per
/// plugin load, so every inspector — the main one and each pop-out's —
/// pastes what any of them copied.</summary>
public sealed class TransformClipboard
{
    public Transform? Copied { get; set; }
}
