using System;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace Poser.UI;

/// <summary>
/// The ONE way Poser says something transient to the user.
///
/// <para>A transient message is the outcome of a COMPLETED user action — a
/// save that landed, an import that refused, a verb whose target went away.
/// It has no standing state to explain, it would linger until the next action
/// displaced it, and the surface that started the action is often not the one
/// the user is looking at by the time it answers. Those go here, to Dalamud's
/// notification system, which is game-wide, dismissible, and does not occupy a
/// pane's layout.</para>
///
/// <para>What does NOT go here: text a pane renders from its CURRENT state
/// with no action behind it — an empty state, a scan in progress, a running
/// transaction's phase, a per-file typed diagnosis, or a placeholder standing
/// where a thing would be. Those explain an absence IN PLACE and must stay
/// where the absence is.</para>
/// </summary>
public sealed class UserNotices
{
    /// <summary>Every notification wears the plugin's name, because it is
    /// shown outside any Poser window.</summary>
    private const string Title = "Poser";

    private readonly INotificationManager _notifications;

    public UserNotices(INotificationManager notifications) =>
        _notifications = notifications;

    /// <summary>An action completed and the user asked for it to.</summary>
    public void Done(string message) => Post(message, NotificationType.Success);

    /// <summary>An action did not run, and the reason is the user's to fix —
    /// nothing was selected, nothing was stashed, the target went away.
    /// </summary>
    public void Refused(string message) => Post(message, NotificationType.Warning);

    /// <summary>One line of information: nothing failed, nothing was
    /// refused, but the user should know what happened.</summary>
    public void Note(string message) => Post(message, NotificationType.Info);

    /// <summary>An action ran and failed.</summary>
    public void Failed(string message) => Post(message, NotificationType.Error);

    /// <summary>"Verb: detail" — the shape a failed file verb reports in.</summary>
    public void Failed(string verb, string detail) => Failed(verb + ": " + detail);

    public void Refused(string verb, string detail) => Refused(verb + ": " + detail);

    /// <summary>Every notice as it is posted: its kind and its text, for
    /// the action recorder.</summary>
    public event Action<string, string>? Posted;

    private void Post(string message, NotificationType type)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        Posted?.Invoke(type.ToString(), message);
        _notifications.AddNotification(new Notification
        {
            Title = Title,
            Content = message,
            Type = type,
        });
    }
}
