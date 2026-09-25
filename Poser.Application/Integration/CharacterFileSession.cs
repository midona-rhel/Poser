using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Documents.Appearance;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

public sealed class CharacterFileSession(
    ActorIntegrationSession integration,
    ICharacterAppearanceFiles files,
    DisruptiveSteps history,
    ISceneCreation creation,
    ISessionGenerationSource sessions,
    TimeProvider? clock = null) : ICharacterFiles
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private PendingSpawn? _pending;
    private sealed record PendingSpawn(SceneEntityHandle Body, string Path, string? Appearance, long Started);

    public IntegrationResult Import(ActorId actor, string path)
    {
        if (!IsAppearance(path)) return ImportPackage(actor, path);
        var read = files.Read(path);
        return read.Success && read.Value is { } document
            ? ApplyAppearance(actor, document) : new(false, read.Detail);
    }

    public IntegrationResult Export(ActorId actor, string path, string description) =>
        integration.BeginExport(actor, path, description);

    public IntegrationResult Reset(ActorId actor) =>
        history.Run(actor, "Reset MCDF", () => integration.ResetMcdf(actor));

    public SceneCreationResult Spawn(string path)
    {
        if (_pending is { } old && old.Body.Session != sessions.ActiveSessionGeneration)
            _pending = null;
        if (_pending is not null || integration.McdfBusy)
            return new(null, "A character-file import is already pending.");

        // Validate before creating a body, and retain these exact bytes until it binds.
        string? appearance = null;
        if (IsAppearance(path))
        {
            var read = files.Read(path);
            if (!read.Success || read.Value is null) return new(null, read.Detail);
            appearance = read.Value;
        }
        var result = creation.CreateActor(new());
        if (result.Handle is { } body)
            _pending = new(body, path, appearance, _clock.GetTimestamp());
        return result;
    }

    /// <summary>Framework-driven; closing a window cannot suspend or redirect an import.</summary>
    public IntegrationResult? Advance()
    {
        if (_pending is not { } pending) return null;
        if (pending.Body.Session != sessions.ActiveSessionGeneration)
        {
            _pending = null;
            return null;
        }
        if (creation.Resolve(pending.Body, requirePose: pending.Appearance is not null)?.Actor is not { } actor)
        {
            if (_clock.GetElapsedTime(pending.Started) < TimeSpan.FromSeconds(30)) return null;
            _pending = null;
            return IntegrationResult.Fail("The spawned actor never became ready for its character file.");
        }
        _pending = null;
        return pending.Appearance is { } appearance
            ? ApplyAppearance(actor, appearance) : ImportPackage(actor, pending.Path);
    }

    public void CancelPendingSpawn() => _pending = null;

    private IntegrationResult ImportPackage(ActorId actor, string path) =>
        history.Run(actor, "Import character file",
            () => integration.BeginImport(actor, path),
            () => integration.ResetMcdf(actor), asset: path);

    private IntegrationResult ApplyAppearance(ActorId actor, string document)
    {
        var before = integration.GetStateJson(actor);
        if (!before.Success || before.Value is null)
            return new(false, before.Detail, before.AppearanceRefusal);
        var request = files.BuildRequest(before.Value, document);
        if (!request.Success || request.Value is null) return new(false, request.Detail);
        var owned = integration.OwnLook(actor);
        if (!owned.Success) return owned;
        return history.Run(actor, "Import character appearance",
            () => integration.ApplyStateJson(actor, request.Value),
            () => integration.ApplyStateJson(actor, before.Value));
    }

    private static bool IsAppearance(string path) =>
        Path.GetExtension(path).Equals(".chara", StringComparison.OrdinalIgnoreCase);
}
