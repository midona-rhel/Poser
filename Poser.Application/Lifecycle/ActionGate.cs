namespace Poser.Application.Lifecycle;

/// <summary>How much of the interface an action holds while it runs.</summary>
public enum ActionKind
{
    /// <summary>A drag that is being held. The mouse is busy, so only shortcuts are ignored.</summary>
    Drag,

    /// <summary>Done within a few frames, such as a bake. Clicks and shortcuts are ignored.</summary>
    Quick,

    /// <summary>Takes time, such as loading a scene. Clicks and shortcuts are ignored and the
    /// interface is greyed out.</summary>
    Long,
}

/// <summary>An action that is running, by the name the interface shows for it.</summary>
public readonly record struct RunningAction(string Name, ActionKind Kind);

/// <summary>
/// The one answer to "is Poser in the middle of something?". While an action runs, a person
/// cannot start another one: the interface ignores clicks and shortcuts, and greys itself
/// out when the action takes time. Poser's own work each frame, and the steps inside the
/// running action, never ask.
/// Each kind of action registers how to tell that it is running. Nothing is switched on and
/// off by hand, so the interface cannot stay blocked after an action has ended.
/// </summary>
public sealed class ActionGate
{
    private readonly List<Func<RunningAction?>> _sources = new();

    /// <summary>Adds one kind of action. The function returns the action while it runs and
    /// null otherwise.</summary>
    public void Register(Func<RunningAction?> source) => _sources.Add(source);

    /// <summary>The action that is running, or null. When several run at once, the one that
    /// holds the most is given.</summary>
    public RunningAction? Running
    {
        get
        {
            RunningAction? held = null;
            foreach (var source in _sources)
                if (source() is { } action && (held is not { } current || action.Kind > current.Kind))
                    held = action;
            return held;
        }
    }
}
