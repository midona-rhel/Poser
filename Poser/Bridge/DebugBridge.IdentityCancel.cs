#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private readonly List<object> _mcdfPresentationTrace = new();
    private string? _lastMcdfPresentationSignature;

    private string PenumbraIdentity(ActorId id, global::Poser.Game.Entities.IActor actor)
    {
        var resolution = (global::Poser.Application.Integration.IIntegrationResolutionPort)_services.GetService(
            typeof(global::Poser.Application.Integration.IIntegrationResolutionPort))!;
        var name = resolution.GetActorName(id);
        var reference = _objects.CreateObjectReference(actor.Address);
        var assignment = _penumbra.GetCollectionAssignment(id);
        return Json(new
        {
            actor = id.ToString(),
            actorManagerName = actor.Name,
            nativeObjectIndex = reference?.ObjectIndex,
            nativeObjectName = reference?.Name.TextValue,
            productionIntegrationName = new { name.Success, name.Detail, name.Value },
            penumbra = new { assignment.Success, assignment.Detail, assignment.Value },
        });
    }

    private string Mcdf(Dictionary<string, string> query, ActorId id)
    {
        object? command = null;
        if (query.TryGetValue("resolve", out var resourcePath))
            command = _penumbra.DebugResolveResourcePath(id, resourcePath);
        else if (query.TryGetValue("spawn", out var packagePath))
            command = _characterFiles.Spawn(packagePath);
        else if (query.GetValueOrDefault("reset") == "1")
            command = _characterFiles.Reset(id);
        else if (query.TryGetValue("export", out var destination))
        {
            if (File.Exists(destination))
                command = new { Success = false, Detail = "Refusing to overwrite an existing file." };
            else
                command = _characterFiles.Export(id, destination, "Local compatibility check");
        }
        else if (query.TryGetValue("import", out var source))
        {
            _mcdfPresentationTrace.Clear();
            _lastMcdfPresentationSignature = null;
            command = _characterFiles.Import(id, source);
        }
        else if (query.TryGetValue("cancel", out var cancel))
        {
            if (!TryBoolean(cancel, out var requested) || !requested)
                return Json(new { accepted = false, detail = "cancel must be true or 1." });
            bool wasBusy = _characterFiles.Busy;
            _characterFiles.Cancel();
            CaptureMcdfPresentationFrame();
            var atCancel = _characterFiles.Progress;
            _mcdfPresentationTrace.Add(new
            {
                frame = Dalamud.Bindings.ImGui.ImGui.GetFrameCount(),
                utc = DateTime.UtcNow,
                eventName = "cancel-command",
                busy = _characterFiles.Busy,
                phase = atCancel?.Phase.ToString(),
                phaseLabel = atCancel is null ? null : global::Poser.UI.AppearancePane.PhaseLabel(atCancel.Phase),
                cancellable = atCancel?.Cancellable,
                receiptState = _characterFiles.Receipt?.State.ToString(),
                receiptDetail = _characterFiles.Receipt?.Detail,
            });
            command = new { accepted = wasBusy, applied = wasBusy,
                detail = wasBusy ? null : "No MCDF operation is running." };
        }
        CaptureMcdfPresentationFrame();
        var progress = _characterFiles.Progress;
        return Json(new
        {
            command,
            busy = _characterFiles.Busy,
            progress,
            receipt = _characterFiles.Receipt,
            presentation = McdfPresentation(progress),
            presentationTrace = _mcdfPresentationTrace,
            owned = Owned(id),
            collection = _penumbra.GetCollectionAssignment(id),
        });
    }

    private object McdfPresentation(McdfProgress? progress) => new
    {
        visible = _characterFiles.Busy && progress is not null,
        phaseLabel = progress is null ? null : global::Poser.UI.AppearancePane.PhaseLabel(progress.Phase),
        progress?.FileName,
        cancelVisible = _characterFiles.Busy && progress is not null,
        cancelEnabled = _characterFiles.Busy && progress?.Cancellable == true,
        terminalState = _characterFiles.Receipt?.State.ToString(),
        terminalDetail = _characterFiles.Receipt?.Detail,
    };

    private void CaptureMcdfPresentationFrame()
    {
        var progress = _characterFiles.Progress;
        var receipt = _characterFiles.Receipt;
        string signature = string.Join("|", _characterFiles.Busy,
            progress?.Phase, progress?.Cancellable, receipt?.State, receipt?.Detail);
        if (signature == _lastMcdfPresentationSignature)
            return;
        _lastMcdfPresentationSignature = signature;
        _mcdfPresentationTrace.Add(new
        {
            frame = Dalamud.Bindings.ImGui.ImGui.GetFrameCount(),
            utc = DateTime.UtcNow,
            busy = _characterFiles.Busy,
            phase = progress?.Phase.ToString(),
            phaseLabel = progress is null ? null : global::Poser.UI.AppearancePane.PhaseLabel(progress.Phase),
            cancellable = progress?.Cancellable,
            receiptState = receipt?.State.ToString(),
            receiptDetail = receipt?.Detail,
        });
        if (_mcdfPresentationTrace.Count > 128)
            _mcdfPresentationTrace.RemoveAt(0);
    }
}
#endif
