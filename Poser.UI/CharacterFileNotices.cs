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
///
/// Every job's end is also written to the log, whoever started it, with how far
/// it got, so an issue report shows why an import did not work.
/// </summary>
internal sealed class CharacterFileNotices(ICharacterFiles files, UserNotices notices,
    Dalamud.Plugin.Services.IPluginLog log)
{
    private Guid? _seen;

    /// <summary>Checks once per frame whether a job ended since the last check.</summary>
    public void Pump()
    {
        if (files.Receipt is not { } receipt || receipt.State == OperationReceiptState.Pending
            || receipt.OperationId == _seen)
            return;
        _seen = receipt.OperationId;
        var job = files.Progress;
        string action = $"{(job?.Kind == McdfOperationKind.Export ? "Export" : "Apply")} {job?.FileName}";
        string line = $"[MCDF] {action}: {receipt.State}, at {job?.Phase}, "
            + $"{job?.FilesDone}/{job?.FilesTotal} files, {job?.BytesDone / 1048576}/{job?.BytesTotal / 1048576} MB"
            + (receipt.OperationId == files.Started ? "" : " (started by a scene or undo)")
            + (string.IsNullOrWhiteSpace(receipt.Detail) ? "" : $". {receipt.Detail}");
        if (receipt.State == OperationReceiptState.Failed) log.Warning(line); else log.Information(line);
        if (receipt.OperationId != files.Started
            || receipt.State is OperationReceiptState.Applied or OperationReceiptState.Cancelled)
            return;
        notices.Report(new Outcome(false, receipt.Detail), action);
    }
}
