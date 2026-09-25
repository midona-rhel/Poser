namespace Poser.Core;

// Portable notifications only. Native payload events stay in Game.
public interface IEvent { }
public record GPoseStateChangedEvent(bool IsGPosing) : IEvent;
public record GazeStateChangedEvent : IEvent;
