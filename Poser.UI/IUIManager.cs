using System;

namespace Poser.UI;

public interface IUIManager : IDisposable
{
    /// <summary>
    /// Toggles the main window visibility.
    /// </summary>
    void ToggleMainWindow();
}
