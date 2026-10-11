namespace Poser.Application.Presentation;

/// <summary>The transient-message channel below the UI: application and game
/// owners report a completed action's outcome here; the UI posts it.</summary>
public interface IUserNotices
{
    /// <summary>Nothing failed, but the user should know what happened.</summary>
    void Note(string message);

    /// <summary>The action did not run, and the reason is the user's to fix.</summary>
    void Refused(string message);

    /// <summary>The action ran and failed.</summary>
    void Failed(string message);
}
