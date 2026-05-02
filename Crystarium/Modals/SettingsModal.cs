using System.Numerics;
using Poser.UI.Controls;
using Poser.UI.Panes;

namespace Poser.UI.Modals;

/// <summary>
/// Settings modal using reusable Modal and TabbedPanel controllers.
/// </summary>
public class SettingsModal
{
    private readonly Modal _modal;
    private readonly TabbedPanel _tabs;

    public SettingsModal()
    {
        _tabs = new TabbedPanel(
            new SkeletonSettingsPane(),
            new DisplaySettingsPane(),
            new UISettingsPane()
        );

        _modal = new Modal("Settings", new Vector2(650, 420));
    }

    public void Open() => _modal.Open();

    public void Draw() => _modal.Draw(dl => _tabs.Draw(dl));
}
