using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state: gizmo settings and posing mode.
/// UI components call methods directly; this class publishes result events.
///
/// NOTE: Selection is handled by ISelectionService, not here.
/// This class only tracks editor tool settings.
/// </summary>
public class EditorState : IEditorState
{
    private readonly IAnimationService _animationService;
    private readonly IGazeService _gazeService;
    private readonly IEventBus _eventBus;

    public TransformPivot TransformPivot { get; set; } = TransformPivot.Individual;
    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;
    public bool IsPosingMode { get; private set; } = false;

    // Lazy inject to avoid circular dependency
    private IActorManager? _actorManager;
    public void SetActorManager(IActorManager actorManager) => _actorManager = actorManager;

    public EditorState(IAnimationService animationService, IGazeService gazeService, IEventBus eventBus)
    {
        _animationService = animationService;
        _gazeService = gazeService;
        _eventBus = eventBus;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing && IsPosingMode)
        {
            ExitPosingMode();
        }
    }

    #region Posing Mode

    public void EnterPosingMode()
    {
        if (IsPosingMode || _actorManager == null)
            return;

        IsPosingMode = true;

        foreach (var actor in _actorManager.Actors)
        {
            // Freeze animation
            if (!_animationService.IsFrozen(actor))
            {
                _animationService.Freeze(actor);
            }

            // Lock gaze to prevent head/eyes from tracking
            _gazeService.LockGaze(actor, GazeTargetType.All);
        }

        _eventBus.Publish(new PosingModeChangedEvent(true));
    }

    public void ExitPosingMode()
    {
        if (!IsPosingMode || _actorManager == null)
            return;

        IsPosingMode = false;

        foreach (var actor in _actorManager.Actors)
        {
            // Unfreeze animation
            if (_animationService.IsFrozen(actor))
            {
                _animationService.Unfreeze(actor);
            }

            // Unlock gaze to allow normal tracking
            _gazeService.UnlockGaze(actor);
        }

        _eventBus.Publish(new PosingModeChangedEvent(false));
    }

    public void TogglePosingMode()
    {
        if (IsPosingMode)
            ExitPosingMode();
        else
            EnterPosingMode();
    }

    #endregion
}
