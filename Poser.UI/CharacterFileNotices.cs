using System;
using Poser.Application.Integration;
using Poser.Domain;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.UI;

/// <summary>
/// Tells the person how each character file job ended, whichever button started
/// it (library, Appearance tab, spawn from a file, undo): a job that fails while it
/// runs gets one notice naming the file. A cancel the person chose gets none. A
/// refusal to start is reported by the button itself, and the Appearance tab's row
/// keeps showing the last result.
/// </summary>
internal sealed class CharacterFileNotices(ICharacterFiles files, UserNotices notices)
{
    private Guid? _seen;

    /// <summary>Checks once per frame whether a job ended since the last check.</summary>
    public void Pump()
    {
        if (files.Receipt is not { } receipt || receipt.State == OperationReceiptState.Pending
            || receipt.OperationId == _seen)
            return;
        _seen = receipt.OperationId;
        if (receipt.State is OperationReceiptState.Applied or OperationReceiptState.Cancelled)
            return;
        string action = files.Progress is { } job
            ? $"{(job.Kind == McdfOperationKind.Export ? "Export" : "Apply")} {job.FileName}"
            : "Character file";
        notices.Report(new Outcome(false, receipt.Detail), action);
    }
}
