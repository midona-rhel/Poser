using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.History;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for animation controls in the properties panel.
/// </summary>
public class AnimationTabPane : ITabPane
{
    private readonly IAnimationService _animationService;
    private readonly AnimationWidget _animationWidget;
    private readonly GazeWidget _gazeWidget;

    // Current entity context (set before Draw)
    private IEntity? _entity;

    public string Name => "Animation";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.Walking;

    public AnimationTabPane(
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        IActorManager actorManager,
        ICameraService cameraService,
        ITextureProvider textureProvider)
    {
        _animationService = animationService;
        _animationWidget = new AnimationWidget(animationService, animationDataService, historyService, textureProvider);
        _gazeWidget = new GazeWidget(gazeService, actorManager, cameraService);
    }

    /// <summary>
    /// Sets the entity to display/edit. Call before Draw().
    /// </summary>
    public void SetEntity(IEntity? entity)
    {
        _entity = entity;
    }

    /// <summary>
    /// Whether this tab is enabled for the current entity.
    /// </summary>
    public bool IsEnabled => _entity is IAnimatable animatable && animatable.CanControlAnimation;

    public void Draw()
    {
        var actor = _entity as IActor;
        bool isFrozen = actor != null && _animationService.IsFrozen(actor);

        DrawSectionHeader("Animation", isFirst: true);
        _animationWidget.DrawAnimationSection(actor);

        DrawSectionHeader("Playback");
        _animationWidget.DrawSpeedSection(actor);
        _animationWidget.DrawScrubSection(actor, isFrozen);

        DrawSectionHeader("Gaze");
        _gazeWidget.Draw(actor);
    }

    private static void DrawSectionHeader(string text, bool isFirst = false)
    {
        if (!isFirst)
            PoserUI.Separator();

        using var row = PoserUI.Row(ImGui.GetTextLineHeight());
        row.Header(text);
    }
}
