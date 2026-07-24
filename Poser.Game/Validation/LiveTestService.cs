using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Core;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Posing;
using Poser.Game.Transforms;
using Poser.Services;
using DomainOperation = Poser.Domain.Transforms.TransformOperation;
using DomainSpace = Poser.Domain.Transforms.TransformSpace;

namespace Poser.Game.Validation;

/// <summary>
/// Focused in-game gate for the clean posing rewrite. Feature diagnostics and
/// UI acceptance deliberately live outside this service.
/// </summary>
public sealed class LiveTestService : ILiveTestService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPose;
    private readonly IActorManager _actors;
    private readonly IActorSpawnService _spawn;
    private readonly IPosingService _actorPosing;
    private readonly ISkeletonService _skeletons;
    private readonly IBonePosingService _posing;
    private readonly IAnimationService _animation;
    private readonly ISelectionService _selection;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly CleanPoseFacade _cleanPose;
    private readonly LiveTestRunStore _runStore;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _runCancellation;
    private LiveTestCancellationKind _cancellationKind;
    private StreamWriter? _eventWriter;
    private IActor? _testActor;
    private ISkeleton? _testSkeleton;
    private readonly List<IActor> _ownedActors = new();
    private readonly List<IEntity> _baselineSelection = new();
    private int _iteration;
    private string _scenarioId = "";

    public bool IsRunning { get; private set; }
    public string? LastRunDirectory { get; private set; }
    public LiveTestRunReport? LastRun { get; private set; }

    public LiveTestService(
        IPluginLog log,
        IFramework framework,
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPose,
        IActorManager actors,
        IActorSpawnService spawn,
        IPosingService actorPosing,
        ISkeletonService skeletons,
        IBonePosingService posing,
        IAnimationService animation,
        ISelectionService selection,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose)
    {
        _log = log;
        _framework = framework;
        _pluginInterface = pluginInterface;
        _gPose = gPose;
        _actors = actors;
        _spawn = spawn;
        _actorPosing = actorPosing;
        _skeletons = skeletons;
        _posing = posing;
        _animation = animation;
        _selection = selection;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _runStore = new LiveTestRunStore(
            pluginInterface.GetPluginConfigDirectory());
        LastRun = _runStore.RecoverLatestInterrupted();
        LastRunDirectory = LastRun?.ArtifactDirectory;
    }

    public async Task<LiveTestRunReport> RunAsync(
        LiveTestOptions options,
        Action<string>? progress = null)
    {
        if (IsRunning)
            throw new InvalidOperationException(
                "A live-test run is already active.");
        _lifetime.Token.ThrowIfCancellationRequested();

        var iterations = Math.Clamp(options.Iterations, 1, 100);
        var steps = Steps();
        var selected = steps
            .Where(step => Matches(options.Selector, step))
            .ToArray();
        var expectedExecutions = selected.Length * iterations;
        var results = new List<LiveTestResult>();
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var startedUtc = DateTimeOffset.UtcNow;

        _runCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cancellationKind = LiveTestCancellationKind.None;
        IsRunning = true;
        LastRunDirectory = Path.Combine(
            _pluginInterface.GetPluginConfigDirectory(),
            "live-tests",
            runId);
        Directory.CreateDirectory(LastRunDirectory);
        Directory.CreateDirectory(
            Path.Combine(LastRunDirectory, "snapshots"));
        LastRun = CreateRunReport(
            runId,
            startedUtc,
            null,
            LiveTestRunOutcome.Running,
            "Starting focused rewrite gate.",
            options,
            expectedExecutions,
            results);
        _runStore.Write(LastRun);

        LiveTestRunOutcome? forcedOutcome = null;
        string? terminalDetail = null;
        try
        {
            _eventWriter = new StreamWriter(
                Path.Combine(LastRunDirectory, "events.jsonl"),
                append: false,
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            WriteEvent("run-start", new
            {
                runId,
                startedUtc,
                options.Selector,
                iterations,
                selected = selected.Select(step => step.Id).ToArray(),
                pluginVersion =
                    typeof(LiveTestService).Assembly.GetName().Version
                        ?.ToString(),
            });

            if (!_gPose.IsGPosing)
            {
                AddResult(
                    results,
                    new LiveTestResult(
                        "setup.gpose",
                        "setup",
                        "GPose precondition",
                        0,
                        false,
                        "Enter GPose first.",
                        0,
                        null,
                        null,
                        Array.Empty<string>()),
                    options,
                    runId,
                    startedUtc,
                    expectedExecutions);
            }
            else if (selected.Length == 0)
            {
                AddResult(
                    results,
                    new LiveTestResult(
                        "coverage.selector",
                        "coverage",
                        "selected rewrite scenario",
                        0,
                        false,
                        $"No focused scenario matches '{options.Selector}'.",
                        0,
                        null,
                        null,
                        Array.Empty<string>()),
                    options,
                    runId,
                    startedUtc,
                    expectedExecutions);
            }
            else
            {
                for (_iteration = 1; _iteration <= iterations; _iteration++)
                {
                    RunToken.ThrowIfCancellationRequested();
                    _testActor = null;
                    _testSkeleton = null;
                    _ownedActors.Clear();
                    _baselineSelection.Clear();
                    _baselineSelection.AddRange(_selection.Selected);

                    var baseline = await CaptureSnapshot(
                        "cycle-baseline",
                        "setup.cycle",
                        _iteration);
                    PersistSnapshot(baseline);

                    try
                    {
                        if (!await EnsureControlledActor())
                        {
                            AddResult(
                                results,
                                new LiveTestResult(
                                    "setup.controlled-actor",
                                    "setup",
                                    "controlled actor",
                                    _iteration,
                                    false,
                                    "Could not create a controlled actor with a live skeleton.",
                                    0,
                                    null,
                                    null,
                                    Array.Empty<string>()),
                                options,
                                runId,
                                startedUtc,
                                expectedExecutions);
                            continue;
                        }

                        foreach (var step in selected)
                        {
                            RunToken.ThrowIfCancellationRequested();
                            _scenarioId = step.Id;
                            progress?.Invoke(
                                $"[{_iteration}/{iterations}] {step.Id}…");
                            var before = await CaptureSnapshot(
                                "before",
                                step.Id,
                                _iteration);
                            var beforePath = PersistSnapshot(before);
                            WriteEvent("scenario-action-pending", new
                            {
                                step.Id,
                                iteration = _iteration,
                                beforeSnapshot = before.SnapshotId,
                            });

                            var stopwatch = Stopwatch.StartNew();
                            bool passed;
                            string detail;
                            try
                            {
                                (passed, detail) = await step.Action()
                                    .WaitAsync(
                                        TimeSpan.FromSeconds(35),
                                        RunToken);
                            }
                            catch (OperationCanceledException)
                                when (RunToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (TimeoutException)
                            {
                                passed = false;
                                detail = "Timed out.";
                            }
                            catch (Exception ex)
                            {
                                passed = false;
                                detail =
                                    $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
                                _log.Error(
                                    $"Focused live scenario '{step.Id}' failed: {ex}");
                            }

                            await WaitFrames(2);
                            var after = await CaptureSnapshot(
                                "after",
                                step.Id,
                                _iteration);
                            var afterPath = PersistSnapshot(after);
                            var invariants = ValidateInvariants(before, after);
                            if (invariants.Count > 0)
                            {
                                passed = false;
                                detail +=
                                    $" | {invariants.Count} invariant failure(s)";
                            }
                            stopwatch.Stop();

                            var result = new LiveTestResult(
                                step.Id,
                                step.Group,
                                step.Name,
                                _iteration,
                                passed,
                                detail,
                                stopwatch.Elapsed.TotalMilliseconds,
                                beforePath,
                                afterPath,
                                invariants);
                            AddResult(
                                results,
                                result,
                                options,
                                runId,
                                startedUtc,
                                expectedExecutions);
                            WriteEvent("scenario-result", result);
                            if (!passed)
                                progress?.Invoke($"  ✗ {step.Id}: {detail}");
                        }
                    }
                    finally
                    {
                        if (!RunToken.IsCancellationRequested)
                        {
                            await Cleanup();
                            var cleanup = await CaptureSnapshot(
                                "cycle-cleanup",
                                "setup.cycle",
                                _iteration);
                            var cleanupPath = PersistSnapshot(cleanup);
                            var cleanupFailures =
                                ValidateCleanup(baseline, cleanup);
                            if (cleanupFailures.Count > 0)
                            {
                                var cleanupResult = new LiveTestResult(
                                    "setup.cleanup",
                                    "setup",
                                    "controlled cleanup",
                                    _iteration,
                                    false,
                                    string.Join("; ", cleanupFailures),
                                    0,
                                    null,
                                    cleanupPath,
                                    cleanupFailures);
                                AddResult(
                                    results,
                                    cleanupResult,
                                    options,
                                    runId,
                                    startedUtc,
                                    expectedExecutions);
                                WriteEvent(
                                    "scenario-result",
                                    cleanupResult);
                            }
                        }
                    }
                }
            }

            var registered = steps
                .Select(step => step.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var catalogDrift = LiveScenarioCatalog.Executable
                .Where(id => !registered.Contains(id))
                .Concat(registered.Where(id =>
                    !LiveScenarioCatalog.Executable.Contains(
                        id,
                        StringComparer.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (catalogDrift.Length > 0)
            {
                AddResult(
                    results,
                    new LiveTestResult(
                        "coverage.catalog",
                        "coverage",
                        "focused catalog parity",
                        0,
                        false,
                        $"Catalog/implementation drift: {string.Join(", ", catalogDrift)}",
                        0,
                        null,
                        null,
                        catalogDrift),
                    options,
                    runId,
                    startedUtc,
                    expectedExecutions);
            }
        }
        catch (OperationCanceledException)
            when (RunToken.IsCancellationRequested)
        {
            var interrupted =
                _cancellationKind == LiveTestCancellationKind.ServiceDisposed;
            forcedOutcome = interrupted
                ? LiveTestRunOutcome.Interrupted
                : LiveTestRunOutcome.Cancelled;
            terminalDetail = interrupted
                ? "Plugin unloaded or reloaded before completion."
                : "Cancelled by the user.";
            WriteEvent(
                interrupted ? "run-interrupted" : "run-cancelled",
                new { _iteration, _scenarioId });
        }
        catch (Exception ex)
        {
            forcedOutcome = LiveTestRunOutcome.RunnerError;
            terminalDetail =
                $"Harness error: {ex.GetType().Name}: {ex.Message}";
            _log.Error($"Focused live runner failed: {ex}");
            WriteEvent(
                "run-error",
                new { _iteration, _scenarioId, exception = ex.ToString() });
        }
        finally
        {
            try
            {
                var outcome = forcedOutcome ??
                              DetermineOutcome(
                                  results,
                                  expectedExecutions);
                terminalDetail ??= DescribeOutcome(
                    outcome,
                    results,
                    expectedExecutions);
                LastRun = CreateRunReport(
                    runId,
                    startedUtc,
                    DateTimeOffset.UtcNow,
                    outcome,
                    terminalDetail,
                    options,
                    expectedExecutions,
                    results);
                _runStore.Write(LastRun);
                WriteEvent("run-end", new
                {
                    outcome = LastRun.Outcome.ToString(),
                    LastRun.IsSuccessful,
                    LastRun.AcceptanceQualified,
                    LastRun.CompletedScenarioExecutions,
                    LastRun.ExpectedScenarioExecutions,
                });
                WriteSummary(LastRun);
            }
            catch (Exception ex)
            {
                _log.Error($"Focused live runner finalization failed: {ex}");
                LastRun = CreateRunReport(
                    runId,
                    startedUtc,
                    DateTimeOffset.UtcNow,
                    LiveTestRunOutcome.RunnerError,
                    $"Harness finalization failed: {ex.Message}",
                    options,
                    expectedExecutions,
                    results);
                try
                {
                    _runStore.Write(LastRun);
                }
                catch (Exception persistenceException)
                {
                    _log.Error(
                        $"Could not persist runner error: {persistenceException}");
                }
            }
            finally
            {
                try
                {
                    _eventWriter?.Dispose();
                }
                catch
                {
                    // Supporting event evidence must not wedge the runner.
                }
                _eventWriter = null;
                IsRunning = false;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
        }

        return LastRun;
    }

    public void Cancel()
    {
        _cancellationKind = LiveTestCancellationKind.User;
        _runCancellation?.Cancel();
    }

    public void Dispose()
    {
        _cancellationKind = LiveTestCancellationKind.ServiceDisposed;
        _lifetime.Cancel();
        _runCancellation?.Cancel();
    }

    private CancellationToken RunToken =>
        _runCancellation?.Token ?? _lifetime.Token;

    private RewriteStep[] Steps() =>
    [
        new(
            "selection.actor-bone-clear",
            "selection",
            "actor, bone, clear",
            SelectionRoundTrip),
        new(
            "transform.actor-components",
            "transform",
            "actor translation, rotation, scale",
            ActorComponents),
        new(
            "transform.actor-undo-redo",
            "transform",
            "actor history roundtrip",
            ActorUndoRedo),
        new(
            "posing.bone-components",
            "posing",
            "bone translation, rotation, scale",
            BoneComponents),
        new(
            "posing.animation-interference",
            "posing",
            "unfrozen animation composition",
            AnimationInterference),
        new(
            "posing.reset-region",
            "posing",
            "pose reset",
            ResetPose),
        new(
            "posing.copy-paste-pose",
            "posing",
            "portable pose transfer",
            CopyPastePose),
    ];

    private Task<(bool Passed, string Detail)> SelectionRoundTrip() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testActor == null || _testSkeleton == null)
                return (false, "Controlled actor is unavailable.");
            var bone = StableTestBone(_testSkeleton);
            if (bone == null)
                return (false, "No stable test bone.");

            _selection.Select(_testActor);
            var actorSelected =
                _selection.Primary?.Id == _testActor.Id &&
                _selection.Selected.Count == 1;
            _selection.Select(bone);
            var boneSelected =
                _selection.Primary?.Id == bone.Id &&
                _selection.Selected.Count == 1;
            _selection.ClearSelection();
            var cleared =
                _selection.Primary == null &&
                _selection.Selected.Count == 0;
            return actorSelected && boneSelected && cleared
                ? (true, "Actor and bone identities resolved; clear removed both.")
                : (false, "Selection session did not preserve single-owner identity.");
        });

    private Task<(bool Passed, string Detail)> ActorComponents() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testActor == null)
                return (false, "Controlled actor is unavailable.");
            var actor = _testActor;
            var cleared = _cleanTransforms.ClearActorOverrides([actor]);
            if (!cleared.Success)
                return (false, cleared.Detail ?? "Actor reset failed.");

            var before = _actorPosing.GetEffectiveTransform(actor);
            var translation = new Vector3(0.125f, -0.075f, 0.05f);
            var translated = ApplyCleanTransform(
                actor,
                DomainOperation.Translate,
                DomainSpace.World,
                TransformDelta.Identity with
                {
                    Translation = translation,
                },
                "Rewrite gate actor translation");
            if (!translated.Success)
                return (false, translated.Detail);
            var afterTranslation =
                _actorPosing.GetEffectiveTransform(actor);
            if (Vector3.Distance(
                    afterTranslation.Position,
                    before.Position + translation) > 0.0005f ||
                !SameRotation(
                    afterTranslation.Rotation,
                    before.Rotation) ||
                Vector3.Distance(
                    afterTranslation.Scale,
                    before.Scale) > 0.0005f)
                return (false, "Actor translation changed another component.");

            var rotation =
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
            var rotated = ApplyCleanTransform(
                actor,
                DomainOperation.Rotate,
                DomainSpace.World,
                TransformDelta.Identity with { Rotation = rotation },
                "Rewrite gate actor rotation");
            if (!rotated.Success)
                return (false, rotated.Detail);
            var afterRotation = _actorPosing.GetEffectiveTransform(actor);
            if (!SameRotation(
                    afterRotation.Rotation,
                    Quaternion.Normalize(
                        rotation * afterTranslation.Rotation)) ||
                Vector3.Distance(
                    afterRotation.Position,
                    afterTranslation.Position) > 0.0005f ||
                Vector3.Distance(
                    afterRotation.Scale,
                    afterTranslation.Scale) > 0.0005f)
                return (false, "Actor rotation changed position or scale.");

            var factor = new Vector3(1.01f, 1.015f, 1.005f);
            var scaled = ApplyCleanTransform(
                actor,
                DomainOperation.Scale,
                DomainSpace.Local,
                TransformDelta.Identity with { ScaleFactor = factor },
                "Rewrite gate actor scale");
            if (!scaled.Success)
                return (false, scaled.Detail);
            var afterScale = _actorPosing.GetEffectiveTransform(actor);
            var expectedScale = afterRotation.Scale * factor;
            return Vector3.Distance(
                       afterScale.Scale,
                       expectedScale) < 0.0005f &&
                   Vector3.Distance(
                       afterScale.Position,
                       afterRotation.Position) < 0.0005f &&
                   SameRotation(
                       afterScale.Rotation,
                       afterRotation.Rotation)
                ? (true, "All actor components used isolated clean gestures.")
                : (false, "Actor scale changed position or rotation.");
        });

    private Task<(bool Passed, string Detail)> ActorUndoRedo() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testActor == null)
                return (false, "Controlled actor is unavailable.");
            var before = _actorPosing.GetEffectiveTransform(_testActor);
            var applied = ApplyCleanTransform(
                _testActor,
                DomainOperation.Translate,
                DomainSpace.World,
                TransformDelta.Identity with
                {
                    Translation = new Vector3(0.035f, -0.02f, 0.015f),
                },
                "Rewrite gate actor history");
            if (!applied.Success)
                return (false, applied.Detail);
            var committed = _actorPosing.GetEffectiveTransform(_testActor);
            var undo = _cleanTransforms.Undo();
            var undone = _actorPosing.GetEffectiveTransform(_testActor);
            var redo = _cleanTransforms.Redo();
            var redone = _actorPosing.GetEffectiveTransform(_testActor);
            return undo.Success &&
                   redo.Success &&
                   SameTransform(before, undone) &&
                   SameTransform(committed, redone)
                ? (true, "One clean patch reproduced before and after states.")
                : (false, undo.Detail ?? redo.Detail ??
                    "Actor history did not roundtrip.");
        });

    private Task<(bool Passed, string Detail)> BoneComponents() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testSkeleton == null)
                return (false, "Controlled skeleton is unavailable.");
            var bone = StableTestBone(_testSkeleton);
            if (bone == null)
                return (false, "No stable test bone.");

            var translation = new Vector3(0.01f, -0.015f, 0.02f);
            var translationResult = ResetAndApplyBone(
                bone,
                DomainOperation.Translate,
                TransformDelta.Identity with
                {
                    Translation = translation,
                });
            var translated = _posing.GetModification(bone);
            if (!translationResult.Success ||
                translated is not { } translationDelta ||
                Vector3.Distance(
                    translationDelta.Position,
                    translation) > 0.0005f ||
                !SameRotation(
                    translationDelta.Rotation,
                    Quaternion.Identity) ||
                translationDelta.Scale.LengthSquared() > 0.000001f)
                return (false, translationResult.Detail ??
                    "Bone translation was not component-isolated.");

            var rotation =
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f);
            var rotationResult = ResetAndApplyBone(
                bone,
                DomainOperation.Rotate,
                TransformDelta.Identity with { Rotation = rotation });
            var rotated = _posing.GetModification(bone);
            if (!rotationResult.Success ||
                rotated is not { } rotationDelta ||
                !SameRotation(rotationDelta.Rotation, rotation) ||
                rotationDelta.Position.LengthSquared() > 0.000001f ||
                rotationDelta.Scale.LengthSquared() > 0.000001f)
                return (false, rotationResult.Detail ??
                    "Bone rotation was not component-isolated.");

            var scaleDelta = new Vector3(0.02f, 0.01f, -0.01f);
            var beforeScale = bone.LastTransform.Scale;
            var scaleResult = ResetAndApplyBone(
                bone,
                DomainOperation.Scale,
                TransformDelta.Identity with
                {
                    ScaleFactor = DivideComponents(
                        beforeScale + scaleDelta,
                        beforeScale),
                });
            var scaled = _posing.GetModification(bone);
            return scaleResult.Success &&
                   scaled is { } scale &&
                   Vector3.Distance(
                       scale.Scale,
                       scaleDelta) < 0.0005f &&
                   scale.Position.LengthSquared() < 0.000001f &&
                   SameRotation(scale.Rotation, Quaternion.Identity)
                ? (true, "All bone components produced isolated pose layers.")
                : (false, scaleResult.Detail ??
                    "Bone scale was not component-isolated.");
        });

    private async Task<(bool Passed, string Detail)> AnimationInterference()
    {
        if (_testActor == null || _testSkeleton == null)
            return (false, "Controlled actor is unavailable.");
        var actor = _testActor;
        var bone = StableTestBone(_testSkeleton);
        if (bone == null)
            return (false, "No stable test bone.");
        var delta =
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);

        var setup = await _framework.RunOnFrameworkThread(() =>
        {
            var reset = _cleanPose.ResetBone(bone);
            if (!reset.Success)
                return (false, reset.Detail ?? "Bone reset failed.");
            _posing.SetBoneIK(bone, BoneIKInfo.Disabled);
            _animation.Unfreeze(actor);
            _animation.ResetSpeed(actor);
            _animation.ApplyBaseAnimation(actor, 253, interrupt: true);
            return ApplyCleanTransform(
                bone,
                DomainOperation.Rotate,
                DomainSpace.Local,
                TransformDelta.Identity with { Rotation = delta },
                "Rewrite gate live animation");
        });
        if (!setup.Item1)
            return (false, setup.Item2);

        try
        {
            var samples = await CollectBoneEvaluations(
                bone,
                minimumSamples: 12,
                maximumFrames: 90);
            var frozen = await _framework.RunOnFrameworkThread(
                () => _animation.IsFrozen(actor));
            var failures = ValidateAnimationComposition(
                samples,
                delta,
                frozen);
            WriteEvent("animation-composition", new
            {
                iteration = _iteration,
                actor = actor.Id.Unique,
                bone = bone.BoneName,
                sampleCount = samples.Count,
                failures,
            });
            return failures.Count == 0
                ? (true,
                    $"{samples.Count} native evaluations preserved one rotation layer over a moving baseline.")
                : (false, string.Join("; ", failures));
        }
        finally
        {
            await _framework.RunOnFrameworkThread(() =>
            {
                _animation.StopBaseAnimation(actor);
                _cleanPose.ResetBone(bone);
            });
        }
    }

    private Task<(bool Passed, string Detail)> ResetPose() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testSkeleton == null)
                return (false, "Controlled skeleton is unavailable.");
            var bone = StableTestBone(_testSkeleton);
            if (bone == null)
                return (false, "No stable test bone.");
            var applied = ResetAndApplyBone(
                bone,
                DomainOperation.Rotate,
                TransformDelta.Identity with
                {
                    Rotation = Quaternion.CreateFromAxisAngle(
                        Vector3.UnitZ,
                        0.2f),
                });
            if (!applied.Success)
                return (false, applied.Detail);
            var reset = _cleanPose.Reset(
                _testSkeleton,
                PoseRegion.All);
            return reset.Success &&
                   _posing.GetModification(bone) == null
                ? (true, $"{reset.Affected} pose targets reset atomically.")
                : (false, reset.Detail ??
                    "Pose layer remained after reset.");
        });

    private Task<(bool Passed, string Detail)> CopyPastePose() =>
        _framework.RunOnFrameworkThread(() =>
        {
            if (_testSkeleton == null)
                return (false, "Controlled skeleton is unavailable.");
            var bone = StableTestBone(_testSkeleton);
            if (bone == null)
                return (false, "No stable test bone.");
            var expected =
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f);
            var applied = ResetAndApplyBone(
                bone,
                DomainOperation.Rotate,
                TransformDelta.Identity with { Rotation = expected });
            if (!applied.Success)
                return (false, applied.Detail);
            var copied = _cleanPose.Copy(_testSkeleton);
            if (!copied.Success || copied.Pose == null)
                return (false, copied.Detail ??
                    "Portable pose capture failed.");
            var reset = _cleanPose.Reset(
                _testSkeleton,
                PoseRegion.All);
            if (!reset.Success)
                return (false, reset.Detail ?? "Pose reset failed.");
            var pasted = _cleanPose.Paste(
                _testSkeleton,
                copied.Pose);
            var actual = _posing.GetModification(bone);
            return pasted.Success &&
                   actual is { } value &&
                   SameRotation(value.Rotation, expected) &&
                   value.Position.LengthSquared() < 0.000001f &&
                   value.Scale.LengthSquared() < 0.000001f
                ? (true,
                    $"{pasted.Affected} portable pose targets restored.")
                : (false, pasted.Detail ??
                    "Portable pose did not restore the isolated rotation.");
        });

    private (bool Success, string Detail) ResetAndApplyBone(
        IBone bone,
        DomainOperation operation,
        TransformDelta delta)
    {
        var reset = _cleanPose.ResetBone(bone);
        if (!reset.Success)
            return (false, reset.Detail ?? "Bone reset failed.");
        return ApplyCleanTransform(
            bone,
            operation,
            DomainSpace.Local,
            delta,
            $"Rewrite gate bone {operation.ToString().ToLowerInvariant()}");
    }

    private (bool Success, string Detail) ApplyCleanTransform(
        IEntity entity,
        DomainOperation operation,
        DomainSpace space,
        TransformDelta delta,
        string description)
    {
        var begin = _cleanTransforms.Begin(
            [entity],
            operation,
            space,
            description: description);
        if (!begin.Success || begin.GestureId is not { } gestureId)
            return (false, begin.Detail ??
                "Clean transform gesture did not start.");
        var first = _cleanTransforms.Update(gestureId, delta);
        var second = first.Success
            ? _cleanTransforms.Update(gestureId, delta)
            : first;
        if (!first.Success || !second.Success)
        {
            _cleanTransforms.Cancel(gestureId);
            return (false, first.Detail ?? second.Detail ??
                "Clean transform update failed.");
        }
        var commit = _cleanTransforms.Commit(gestureId);
        return commit.Success
            ? (true, "Clean gesture committed idempotently.")
            : (false, commit.Detail ?? "Clean transform commit failed.");
    }

    private async Task<bool> EnsureControlledActor()
    {
        _testActor = await _framework.RunOnFrameworkThread(
            () => _spawn.SpawnPlayerClone());
        if (_testActor == null)
            return false;
        _ownedActors.Add(_testActor);
        var actor = _testActor;
        var ready = await WaitFor(
            () => _skeletons.GetSkeleton(actor) is { IsValid: true },
            8000);
        _testSkeleton = _skeletons.GetSkeleton(actor);
        WriteEvent("controlled-actor-ready", new
        {
            actor = actor.Id.Unique,
            skeleton = _testSkeleton?.Id.Unique,
            ready,
        });
        return ready && _testSkeleton != null;
    }

    private async Task Cleanup()
    {
        var ownedIds = _ownedActors
            .Select(actor => actor.Id)
            .ToHashSet();
        await _framework.RunOnFrameworkThread(() =>
        {
            _selection.ClearSelection();
            foreach (var actor in _ownedActors.ToArray())
            {
                _animation.StopBaseAnimation(actor);
                _actorPosing.ClearTransformOverride(actor);
                if (_spawn.IsSpawnedActor(actor))
                    _spawn.DestroyActor(actor);
            }

            foreach (var baseline in _baselineSelection)
            {
                var current = ResolveCurrentEntity(baseline);
                if (current == null)
                    continue;
                if (_selection.Selected.Count == 0)
                    _selection.Select(current);
                else
                    _selection.AddToSelection(current);
            }
        });
        await WaitFor(
            () => _actors.Actors.All(actor =>
                !ownedIds.Contains(actor.Id)),
            5000);
        _testActor = null;
        _testSkeleton = null;
    }

    private IEntity? ResolveCurrentEntity(IEntity baseline)
    {
        if (baseline is IActor)
            return _actors.Actors.FirstOrDefault(
                actor => actor.Id == baseline.Id);
        if (baseline is not IBone bone)
            return null;
        var actor = _actors.Actors.FirstOrDefault(
            candidate => candidate.Id == bone.Skeleton.Actor.Id);
        return actor == null
            ? null
            : _skeletons.GetSkeleton(actor)?.GetBone(
                bone.PartialId,
                bone.BoneIndex);
    }

    private IBone? StableTestBone(ISkeleton skeleton) =>
        skeleton.GetBone("j_ude_b_r") ??
        skeleton.GetBone("j_kosi") ??
        skeleton.Bones.FirstOrDefault(bone => !bone.IsPartialRoot);

    private async Task<IReadOnlyList<BoneEvaluationObservation>>
        CollectBoneEvaluations(
            IBone bone,
            int minimumSamples,
            int maximumFrames)
    {
        var samples =
            new List<BoneEvaluationObservation>(minimumSamples);
        long lastSequence = -1;
        for (var frame = 0;
             frame < maximumFrames && samples.Count < minimumSamples;
             frame++)
        {
            await WaitFrames(1);
            var observation =
                await _framework.RunOnFrameworkThread(() =>
                    _posing.TryGetEvaluationObservation(
                        bone,
                        out var current)
                        ? current
                        : (BoneEvaluationObservation?)null);
            if (observation is not { } sample ||
                sample.Sequence == lastSequence)
                continue;
            lastSequence = sample.Sequence;
            samples.Add(sample);
        }
        return samples;
    }

    private static List<string> ValidateAnimationComposition(
        IReadOnlyList<BoneEvaluationObservation> samples,
        Quaternion expectedRotation,
        bool frozen)
    {
        var failures = new List<string>();
        if (frozen)
            failures.Add("Actor animation was frozen.");
        if (samples.Count < 12)
            failures.Add(
                $"Expected 12 native evaluations, captured {samples.Count}.");
        if (samples.Count == 0)
            return failures;

        var firstBaseline = samples[0].AnimatedBaseline;
        if (!samples.Skip(1).Any(sample =>
                !SameTransform(
                    firstBaseline,
                    sample.AnimatedBaseline,
                    0.0001f)))
            failures.Add("Animated native baseline did not move.");

        long sequence = -1;
        foreach (var sample in samples)
        {
            if (sample.Sequence <= sequence)
                failures.Add("Native observation sequence did not advance.");
            sequence = sample.Sequence;
            if (sample.StackCount != 1)
                failures.Add(
                    $"Expected one pose layer, found {sample.StackCount}.");
            if (!IsFinite(sample.AnimatedBaseline) ||
                !IsFinite(sample.EvaluatedTransform) ||
                !IsFinite(sample.AppliedDelta))
            {
                failures.Add("Native observation contains non-finite state.");
                continue;
            }
            if (!SameRotation(
                    sample.AppliedDelta.Rotation,
                    expectedRotation) ||
                sample.AppliedDelta.Position.LengthSquared() > 0.000001f ||
                sample.AppliedDelta.Scale.LengthSquared() > 0.000001f)
                failures.Add("Persistent pose layer changed between frames.");

            var expected = new Transform
            {
                Position =
                    sample.AnimatedBaseline.Position +
                    sample.AppliedDelta.Position,
                Rotation = Quaternion.Normalize(
                    sample.AnimatedBaseline.Rotation *
                    sample.AppliedDelta.Rotation),
                Scale =
                    sample.AnimatedBaseline.Scale +
                    sample.AppliedDelta.Scale,
            };
            if (!SameTransform(
                    expected,
                    sample.EvaluatedTransform,
                    0.0005f))
                failures.Add(
                    "Evaluated bone did not equal baseline plus pose layer.");
        }
        return failures.Distinct(StringComparer.Ordinal).ToList();
    }

    private Task<LiveTestSnapshot> CaptureSnapshot(
        string phase,
        string scenarioId,
        int iteration) =>
        _framework.RunOnFrameworkThread(() =>
        {
            var actorStates = _actors.Actors
                .Select(actor => new LiveActorState(
                    actor.Id.Unique,
                    actor.Address.ToInt64(),
                    actor.Name,
                    actor.ActorKind.ToString(),
                    actor.IsPosing,
                    actor.IsVisible,
                    LiveTransformState.From(actor.Transform)))
                .ToArray();
            var selection = _selection.Selected
                .Select(entity =>
                    $"{entity.EntityType}:{entity.Id.Unique}")
                .ToArray();
            LiveSkeletonState? skeleton = null;
            if (_testSkeleton != null)
            {
                var bones = _testSkeleton.Bones.Select(bone =>
                    new LiveBoneState(
                        bone.Id.Unique,
                        bone.BoneName,
                        bone.PartialId,
                        bone.BoneIndex,
                        bone.ParentBone?.BoneName,
                        LiveTransformState.From(bone.LastTransform),
                        LiveTransformState.From(bone.LastRawTransform),
                        _posing.CapturePoseStacks(bone)
                            .Select(stack => new LivePoseStackState(
                                stack.PropagateComponents.ToString(),
                                stack.Layer,
                                LiveTransformState.From(
                                    stack.Transform)))
                            .ToArray()))
                    .ToArray();
                skeleton = new LiveSkeletonState(
                    _testSkeleton.Id.Unique,
                    _testSkeleton.Actor.Id.Unique,
                    _testSkeleton.IsValid,
                    bones.Length,
                    bones);
            }
            return new LiveTestSnapshot(
                $"{scenarioId}-{iteration:D2}-{phase}-{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                scenarioId,
                iteration,
                phase,
                actorStates,
                selection,
                skeleton);
        });

    private string PersistSnapshot(LiveTestSnapshot snapshot)
    {
        if (LastRunDirectory == null)
            return "";
        var path = Path.Combine(
            LastRunDirectory,
            "snapshots",
            snapshot.SnapshotId + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(snapshot, JsonOptions));
        return path;
    }

    private static List<string> ValidateInvariants(
        LiveTestSnapshot before,
        LiveTestSnapshot after)
    {
        var failures = new List<string>();
        foreach (var duplicate in after.Actors
                     .GroupBy(actor => actor.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            failures.Add($"Duplicate actor id {duplicate.Key}.");
        foreach (var actor in after.Actors)
            ValidateTransform(
                $"actor[{actor.Id}]",
                actor.Transform,
                failures,
                requireUnitRotation: true);

        if (after.TestSkeleton is { } skeleton)
        {
            if (!skeleton.IsValid)
                failures.Add("Controlled skeleton became invalid.");
            foreach (var duplicate in skeleton.Bones
                         .GroupBy(bone =>
                             (bone.PartialId, bone.BoneIndex))
                         .Where(group => group.Count() > 1))
                failures.Add(
                    $"Duplicate bone {duplicate.Key.PartialId}:{duplicate.Key.BoneIndex}.");
            foreach (var bone in skeleton.Bones)
            {
                ValidateTransform(
                    $"bone[{bone.Id}].current",
                    bone.Transform,
                    failures,
                    requireUnitRotation: false);
                ValidateTransform(
                    $"bone[{bone.Id}].raw",
                    bone.RawTransform,
                    failures,
                    requireUnitRotation: false);
                foreach (var stack in bone.PoseStacks)
                    ValidateTransform(
                        $"bone[{bone.Id}].layer",
                        stack.Transform,
                        failures,
                        requireUnitRotation: true);
            }
        }

        if (before.TestSkeleton is { } beforeSkeleton &&
            after.TestSkeleton is { } afterSkeleton)
        {
            var beforeIds = beforeSkeleton.Bones
                .Select(bone =>
                    (bone.PartialId, bone.BoneIndex, bone.Name))
                .ToHashSet();
            var afterIds = afterSkeleton.Bones
                .Select(bone =>
                    (bone.PartialId, bone.BoneIndex, bone.Name))
                .ToHashSet();
            if (!beforeIds.SetEquals(afterIds))
                failures.Add("Controlled skeleton identity changed.");
        }
        return failures;
    }

    private static void ValidateTransform(
        string path,
        LiveTransformState transform,
        ICollection<string> failures,
        bool requireUnitRotation)
    {
        var values = new[]
        {
            transform.PositionX,
            transform.PositionY,
            transform.PositionZ,
            transform.RotationX,
            transform.RotationY,
            transform.RotationZ,
            transform.RotationW,
            transform.ScaleX,
            transform.ScaleY,
            transform.ScaleZ,
        };
        if (values.Any(value => !float.IsFinite(value)))
        {
            failures.Add($"{path} contains NaN or infinity.");
            return;
        }
        var rotationLength =
            transform.RotationX * transform.RotationX +
            transform.RotationY * transform.RotationY +
            transform.RotationZ * transform.RotationZ +
            transform.RotationW * transform.RotationW;
        if (rotationLength < 0.000001f)
            failures.Add($"{path} contains a zero quaternion.");
        else if (requireUnitRotation &&
                 MathF.Abs(1f - rotationLength) > 0.02f)
            failures.Add(
                $"{path} quaternion length² is {rotationLength:0.000000}.");
    }

    private static List<string> ValidateCleanup(
        LiveTestSnapshot baseline,
        LiveTestSnapshot cleanup)
    {
        var failures = new List<string>();
        if (cleanup.Actors.Count != baseline.Actors.Count)
            failures.Add(
                $"Actor count changed from {baseline.Actors.Count} to {cleanup.Actors.Count}.");
        if (!cleanup.Selection.SequenceEqual(
                baseline.Selection,
                StringComparer.Ordinal))
            failures.Add("Original selection was not restored.");
        return failures;
    }

    private Task<bool> WaitFor(
        Func<bool> condition,
        int timeoutMilliseconds)
    {
        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline =
            Environment.TickCount64 + timeoutMilliseconds;
        void Tick(IFramework framework)
        {
            try
            {
                if (condition())
                {
                    _framework.Update -= Tick;
                    completion.TrySetResult(true);
                }
                else if (Environment.TickCount64 > deadline)
                {
                    _framework.Update -= Tick;
                    completion.TrySetResult(false);
                }
            }
            catch
            {
                _framework.Update -= Tick;
                completion.TrySetResult(false);
            }
        }
        _framework.Update += Tick;
        return Await();

        async Task<bool> Await()
        {
            using var registration = RunToken.Register(() =>
            {
                _framework.Update -= Tick;
                completion.TrySetCanceled(RunToken);
            });
            return await completion.Task;
        }
    }

    private async Task WaitFrames(int frameCount)
    {
        var remaining = frameCount;
        var completion =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void Tick(IFramework framework)
        {
            if (--remaining > 0)
                return;
            _framework.Update -= Tick;
            completion.TrySetResult();
        }
        _framework.Update += Tick;
        try
        {
            await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                RunToken);
        }
        finally
        {
            _framework.Update -= Tick;
        }
    }

    private static bool Matches(
        string? selector,
        RewriteStep step) =>
        string.IsNullOrWhiteSpace(selector) ||
        selector.Equals(
            LiveScenarioCatalog.BasicSelector,
            StringComparison.OrdinalIgnoreCase) &&
        LiveScenarioCatalog.Basic.Contains(
            step.Id,
            StringComparer.OrdinalIgnoreCase) ||
        selector.Equals(
            step.Group,
            StringComparison.OrdinalIgnoreCase) ||
        selector.Equals(
            step.Id,
            StringComparison.OrdinalIgnoreCase);

    private static Vector3 DivideComponents(
        Vector3 numerator,
        Vector3 denominator)
    {
        static float Divide(float left, float right) =>
            MathF.Abs(right) < 0.00001f ? 1f : left / right;
        return new Vector3(
            Divide(numerator.X, denominator.X),
            Divide(numerator.Y, denominator.Y),
            Divide(numerator.Z, denominator.Z));
    }

    private static bool SameRotation(
        Quaternion left,
        Quaternion right) =>
        MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(left),
            Quaternion.Normalize(right))) > 0.9999f;

    private static bool SameTransform(
        Transform left,
        Transform right,
        float epsilon = 0.0005f) =>
        Vector3.Distance(left.Position, right.Position) < epsilon &&
        SameRotation(left.Rotation, right.Rotation) &&
        Vector3.Distance(left.Scale, right.Scale) < epsilon;

    private static bool IsFinite(Transform transform) =>
        float.IsFinite(transform.Position.X) &&
        float.IsFinite(transform.Position.Y) &&
        float.IsFinite(transform.Position.Z) &&
        float.IsFinite(transform.Rotation.X) &&
        float.IsFinite(transform.Rotation.Y) &&
        float.IsFinite(transform.Rotation.Z) &&
        float.IsFinite(transform.Rotation.W) &&
        transform.Rotation.LengthSquared() >= 0.000001f &&
        float.IsFinite(transform.Scale.X) &&
        float.IsFinite(transform.Scale.Y) &&
        float.IsFinite(transform.Scale.Z);

    private void WriteEvent(string kind, object payload)
    {
        if (_eventWriter == null)
            return;
        _eventWriter.WriteLine(JsonSerializer.Serialize(new
        {
            kind,
            timestampUtc = DateTimeOffset.UtcNow,
            payload,
        }));
    }

    private void AddResult(
        ICollection<LiveTestResult> results,
        LiveTestResult result,
        LiveTestOptions options,
        string runId,
        DateTimeOffset startedUtc,
        int expectedExecutions)
    {
        results.Add(result);
        LastRun = CreateRunReport(
            runId,
            startedUtc,
            null,
            LiveTestRunOutcome.Running,
            $"Completed {CompletedExecutions(results)} of {expectedExecutions}.",
            options,
            expectedExecutions,
            results);
        _runStore.Write(LastRun);
    }

    private LiveTestRunReport CreateRunReport(
        string runId,
        DateTimeOffset startedUtc,
        DateTimeOffset? completedUtc,
        LiveTestRunOutcome outcome,
        string? detail,
        LiveTestOptions options,
        int expectedExecutions,
        IEnumerable<LiveTestResult> results)
    {
        var rows = results.ToArray();
        var repetitionMet =
            options.Iterations >= LiveTestOptions.AcceptanceIterations;
        return new LiveTestRunReport(
            LiveTestRunReport.CurrentSchemaVersion,
            runId,
            startedUtc,
            completedUtc,
            outcome,
            detail,
            options,
            expectedExecutions,
            CompletedExecutions(rows),
            rows.Count(result => result.Passed == true),
            rows.Count(result => result.Passed == false),
            rows.Count(result => result.Passed == null),
            repetitionMet,
            outcome == LiveTestRunOutcome.Succeeded &&
            repetitionMet &&
            !string.Equals(
                options.Selector,
                LiveScenarioCatalog.BasicSelector,
                StringComparison.OrdinalIgnoreCase),
            rows,
            LastRunDirectory ??
            Path.Combine(
                _pluginInterface.GetPluginConfigDirectory(),
                "live-tests",
                runId));
    }

    private static int CompletedExecutions(
        IEnumerable<LiveTestResult> results) =>
        results.Count(result =>
            result.Iteration > 0 &&
            result.Group is not "setup" and not "coverage" and not "run");

    private static LiveTestRunOutcome DetermineOutcome(
        IReadOnlyCollection<LiveTestResult> results,
        int expectedExecutions)
    {
        if (results.Any(result => result.Passed == false))
            return LiveTestRunOutcome.Failed;
        if (results.Any(result => result.Passed == null) ||
            CompletedExecutions(results) != expectedExecutions)
            return LiveTestRunOutcome.Incomplete;
        return LiveTestRunOutcome.Succeeded;
    }

    private static string DescribeOutcome(
        LiveTestRunOutcome outcome,
        IReadOnlyCollection<LiveTestResult> results,
        int expectedExecutions) =>
        outcome switch
        {
            LiveTestRunOutcome.Succeeded =>
                $"All {expectedExecutions} focused scenario executions passed.",
            LiveTestRunOutcome.Failed =>
                $"{results.Count(result => result.Passed == false)} result(s) failed.",
            LiveTestRunOutcome.Incomplete =>
                $"Completed {CompletedExecutions(results)} of {expectedExecutions}.",
            LiveTestRunOutcome.Cancelled => "Cancelled by the user.",
            LiveTestRunOutcome.Interrupted => "Interrupted by plugin lifetime.",
            LiveTestRunOutcome.RunnerError => "The focused runner failed.",
            _ => "Run is active.",
        };

    private void WriteSummary(LiveTestRunReport report)
    {
        if (LastRunDirectory == null)
            return;
        try
        {
            File.WriteAllText(
                Path.Combine(LastRunDirectory, "report.json"),
                JsonSerializer.Serialize(report, JsonOptions));
            var summary = new StringBuilder()
                .AppendLine("# Poser focused rewrite gate")
                .AppendLine()
                .AppendLine($"- Run: {report.RunId}")
                .AppendLine($"- Outcome: {report.Outcome}")
                .AppendLine(
                    $"- Executions: {report.CompletedScenarioExecutions}/{report.ExpectedScenarioExecutions}")
                .AppendLine(
                    $"- Acceptance qualified: {(report.AcceptanceQualified ? "yes" : "no")}")
                .AppendLine($"- Detail: {report.Detail}")
                .AppendLine()
                .AppendLine("## Failures")
                .AppendLine();
            var failures = report.Results
                .Where(result => result.Passed == false)
                .ToArray();
            if (failures.Length == 0)
                summary.AppendLine("None.");
            else
                foreach (var failure in failures)
                    summary.AppendLine(
                        $"- `{failure.ScenarioId}` iteration {failure.Iteration}: {failure.Detail}");
            File.WriteAllText(
                Path.Combine(LastRunDirectory, "summary.md"),
                summary.ToString());
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"Focused live report write failed: {ex.Message}");
        }
    }

    private sealed record RewriteStep(
        string Id,
        string Group,
        string Name,
        Func<Task<(bool Passed, string Detail)>> Action);
}

internal enum LiveTestCancellationKind
{
    None,
    User,
    ServiceDisposed,
}
