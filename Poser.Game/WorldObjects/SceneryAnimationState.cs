namespace Poser.Game.WorldObjects;

// Phase-specific data prevents contradictory "paused, engage next, anchored"
// combinations. Watching measures native motion; ReanchorPending crosses exactly
// one native frame after resume. Paused remembers only whether to reanchor.
internal abstract class SceneryAnimationState
{
    private SceneryAnimationState() { }

    internal sealed class Watching : SceneryAnimationState
    {
        public Transform? LastWritten;
    }

    internal sealed class ReanchorPending : SceneryAnimationState;

    internal sealed class Anchored(Transform reference, Transform lastWritten) : SceneryAnimationState
    {
        public Transform Reference { get; } = reference;
        public Transform LastWritten = lastWritten;
    }

    internal sealed class Paused(Transform placement, byte[]? tail, bool resumeAnchored) : SceneryAnimationState
    {
        public Transform Placement = placement;
        public byte[]? Tail = tail;
        public SceneryTailCapture TailCapture = tail != null
            ? SceneryTailCapture.Captured : SceneryTailCapture.WaitingForModel;
        public bool ResumeAnchored { get; } = resumeAnchored;
    }
}

internal enum SceneryTailCapture { WaitingForModel, Captured, Unavailable }
