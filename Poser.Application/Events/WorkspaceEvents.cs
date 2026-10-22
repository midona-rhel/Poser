namespace Poser.Application.Events;

// Portable notifications only. Native payload events stay in Game.
public interface IEvent { }
public record GPoseStateChangedEvent(bool IsGPosing) : IEvent;
/// <summary>Final capture finished; cancel work and restore owned state before actors disappear.</summary>
public record GPoseExitingEvent : IEvent;
public record GazeStateChangedEvent : IEvent;
