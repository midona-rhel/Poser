namespace Poser.UI.Controls;

/// <summary>
/// Interface for a tab pane that can be displayed in a TabbedPanel.
/// All implementations should use UIColors for consistent theming.
/// </summary>
public interface ITabPane
{
    /// <summary>
    /// The display name shown on the tab.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Draws the tab content. Called when this tab is active.
    /// </summary>
    void Draw();
}
