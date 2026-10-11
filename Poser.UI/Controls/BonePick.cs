using System;
using Poser.Domain.Identity;

namespace Poser.UI.Controls;

/// <summary>Overlay bone picking: a surface asks for a bone and the
/// skeleton overlay shows every actor's bones until one is clicked. Single
/// takes the first click; multi keeps going while Ctrl is held on the
/// click. Escape, a right-click or a click on nothing ends it. One instance
/// per plugin load, shared by the asking surfaces and the overlay.</summary>
public sealed class BonePick
{
    public bool Active { get; private set; }
    public bool Multi { get; private set; }
    /// <summary>When set, only this actor's bones show and take.</summary>
    public ActorId? OnlyActor { get; private set; }
    private Action<BoneId>? _onPick;

    public void Begin(bool multi, Action<BoneId> onPick, ActorId? onlyActor = null)
    {
        Active = true;
        Multi = multi;
        OnlyActor = onlyActor;
        _onPick = onPick;
    }

    public void Take(BoneId bone, bool keepGoing)
    {
        _onPick?.Invoke(bone);
        if (!Multi || !keepGoing)
            Cancel();
    }

    public void Cancel()
    {
        Active = false;
        Multi = false;
        OnlyActor = null;
        _onPick = null;
    }
}
