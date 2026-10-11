using Poser.Application.Transforms;

namespace Poser.Application.Tests.Fixtures;

internal static class JournalUndo
{
    public static bool Undo(EditHistory history)
    {
        var step = (JournalStep)history.PeekUndo()!;
        if (!step.Undo())
            return false;
        history.CommitUndo(step);
        return true;
    }
}
