using System;

namespace Poser.Services;

public interface IUIManager : IDisposable
{
    /// <summary>
    /// Toggles the main window visibility.
    /// </summary>
    void ToggleMainWindow();
}
