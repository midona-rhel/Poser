using System;
using Poser.Application.Integration;
using Poser.Domain;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.UI;

/// <summary>
/// Tells the person how a character file job they started ended, whichever button
/// started it (library, Appearance tab Import or Export, spawn from a file): a job
/// that fails while it runs gets one notice naming the file. A cancel the person
/// chose gets none. A refusal to start is reported by the button itself, jobs that
/// scene loads, scene saves and undo start are reported by those, and the
/// Appearance tab's row keeps showing the last result.
/// </summary>
internal sealed class CharacterFileNotices(ICharacterFiles files, UserNotices notices)
{
    private Guid? _seen;

    /// <summary>Checks once per frame whether a job ended since the last check.</summary>
    public void Pump()
    {
        if (files.Receipt is not { } receipt || receipt.State == OperationReceiptState.Pending
            || receipt.OperationId == _seen || receipt.OperationId != files.Started)
            return;
        _seen = receipt.OperationId;
        if (receipt.State is OperationReceiptState.Applied or OperationReceiptState.Cancelled)
            return;
        var job = files.Progress!;
        string action = $"{(job.Kind == McdfOperationKind.Export ? "Export" : "Apply")} {job.FileName}";
        notices.Report(new Outcome(false, receipt.Detail), action);
    }
}
