using System;
using System.Linq;
using Poser.Application.Selection;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace Poser.UI;

/// <summary>
/// The shared path for transient messages to the user.
///
/// <para>A transient message reports the outcome of a completed user action — a
/// save that landed, an import that refused, a verb whose target went away.
/// It has no standing state to explain, it would linger until the next action
/// displaced it, and the surface that started the action is often not the one
/// the user is looking at by the time it answers. Those go here, to Dalamud's
/// notification system, which is game-wide, dismissible, and does not occupy a
/// pane's layout.</para>
///
/// <para>Do not use this for text a pane renders from its current state
/// with no action behind it — an empty state, a scan in progress, a running
/// transaction's phase, a per-file typed diagnosis, or a placeholder standing
/// where a thing would be. Those explain an absence in its surrounding context and must stay
/// where the absence is.</para>
/// </summary>
public sealed class UserNotices : global::Poser.Application.Presentation.IUserNotices
{
    /// <summary>Every notification wears the plugin's name, because it is
    /// shown outside any Poser window.</summary>
    private const string Title = "Poser";

    private readonly INotificationManager _notifications;

    public UserNotices(INotificationManager notifications) =>
        _notifications = notifications;

    /// <summary>An action completed and the user asked for it to.</summary>
    public void Done(string message) => Post(message, NotificationType.Success);

    /// <summary>One line of information: nothing failed, nothing was
    /// refused, but the user should know what happened.</summary>
    public void Note(string message) => Post(message, NotificationType.Info);

    /// <summary>A failure posted as a whole sentence, with no control to
    /// name. Only two callers need it: the Scene page's finished save or load
    /// notice, and an expression the game dropped after a redraw. Everything
    /// a person clicks reports through <see cref="Report"/>.</summary>
    public void Failed(string message) => Post(message, NotificationType.Error);

    /// <summary>Logic below the interface reports refusals as whole
    /// sentences.</summary>
    void global::Poser.Application.Presentation.IUserNotices.Refused(string message) => Refused(message);

    private void Refused(string message) => Post(message, NotificationType.Warning);

    private void Failed(string verb, string detail) => Failed(verb + ": " + detail);

    private void Refused(string verb, string detail) => Refused(verb + ": " + detail);

    /// <summary>The one way the interface tells a person that an action did
    /// not work: nothing when it worked, "Action: reason" when it did not, or
    /// "Action failed." when no reason was given. <paramref name="action"/> is
    /// the name on the button, menu entry or field the person used. Returns
    /// whether it worked. Pass <paramref name="refusal"/> when the action did
    /// not run and the reason is the person's to fix: the notice is then a
    /// yellow warning, not a red error.</summary>
    public bool Report(Poser.Domain.Outcome result, string action, bool refusal = false)
    {
        if (result.Success)
            return true;
        string message = string.IsNullOrWhiteSpace(result.Detail) ? action + " failed." : action + ": " + result.Detail;
        if (refusal) Refused(message); else Failed(message);
        return false;
    }

    public void Removal(SelectionRemovalResult result)
    {
        // A removal that landed can still lose something its undo cannot restore.
        foreach (var note in result.Items
            .Where(item => item.Status == SelectionRemovalStatus.Removed && item.Detail != null)
            .Select(item => item.Detail!).Distinct())
            Note(note);
        foreach (var failure in result.Items
            .Where(item => item.Status is SelectionRemovalStatus.Refused or SelectionRemovalStatus.Failed)
            .Select(item => (item.Status, Detail: item.Detail ?? "The entity could not be removed."))
            .Distinct())
        {
            if (failure.Status == SelectionRemovalStatus.Failed)
                Failed("Remove", failure.Detail);
            else
                Refused("Remove", failure.Detail);
        }
    }

    public void Visibility(SelectionVisibilityResult result)
    {
        foreach (var detail in result.Items
            .Where(item => !item.Result.Success)
            .Select(item => item.Result.Detail ?? "The change was refused.")
            .Distinct())
            Refused("Visibility", detail);
    }

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
