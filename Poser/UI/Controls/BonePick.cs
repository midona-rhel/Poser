using System;
using Poser.Domain.Identity;

namespace Poser.UI.Controls;

/// <summary>Overlay bone picking: a surface asks for a bone and the
/// skeleton overlay shows every actor's bones until one is clicked. Single
/// takes the first click; multi keeps going while Ctrl is held on the
/// click. Escape, a right-click or a click on nothing ends it.</summary>
public static class BonePick
{
    public static bool Active { get; private set; }
    public static bool Multi { get; private set; }
    private static Action<BoneId>? _onPick;

    public static void Begin(bool multi, Action<BoneId> onPick)
    {
        Active = true;
        Multi = multi;
        _onPick = onPick;
    }

    public static void Take(BoneId bone, bool keepGoing)
    {
        _onPick?.Invoke(bone);
        if (!Multi || !keepGoing)
            Cancel();
    }

    public static void Cancel()
    {
        Active = false;
        Multi = false;
        _onPick = null;
    }
}
