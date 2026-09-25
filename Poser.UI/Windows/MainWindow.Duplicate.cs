using Poser.Application.Scene;
using Poser.Domain.Identity;

namespace Poser.UI;

public partial class MainWindow
{
    private static ContextMenuItem[] DuplicateSubmenu(bool posable) =>
    [
        new ContextMenuItem("Duplicate", TablerIcon.Copy),
        new ContextMenuItem("Duplicate with pose", TablerIcon.Stack2, disabled: !posable),
    ];

    private void DuplicateSelection(bool withPose) => _duplication.DuplicateSelection(withPose);
    private void DuplicateGroup(SceneGroup group, bool withPose) => _duplication.DuplicateGroup(group, withPose);
    private void DuplicateAndSelect(SelectionId id, bool withPose = false) => _duplication.DuplicateAndSelect(id, withPose);
}
