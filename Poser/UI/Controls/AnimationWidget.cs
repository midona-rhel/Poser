using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.History;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Widget for controlling animations (base/blend selection, speed, and time scrubbing).
/// Uses 40% opacity for disabled state instead of ImGui disabled.
/// </summary>
public class AnimationWidget
{
    private const float LabelWidth = 50f;

    private readonly IAnimationService _animationService;
    private readonly IAnimationDataService _animationDataService;
    private readonly IHistoryService _historyService;

    // Reusable animation selectors
    private readonly AnimationSelector _baseAnimationSelector;
    private readonly AnimationSelector _blendAnimationSelector;

    // Tracking for slider history
    private float _speedBeforeEdit;
    private bool _isEditingSpeed;

    // Selected animation IDs (in selectors, not yet applied)
    private ushort? _selectedBaseId;
    private ushort? _selectedBlendId;

    // Currently applied animation IDs
    private ushort? _appliedBaseId;
    private ushort? _appliedBlendId;

    // Track actor to clear state on change
    private nint _lastActorAddress;

    public AnimationWidget(
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        ITextureProvider textureProvider)
    {
        _animationService = animationService;
        _animationDataService = animationDataService;
        _historyService = historyService;

        _baseAnimationSelector = new AnimationSelector(animationDataService, textureProvider);
        _blendAnimationSelector = new AnimationSelector(animationDataService, textureProvider);
    }

    /// <summary>
    /// Draws the animation selection section.
    /// </summary>
    /// <param name="actor">The actor to control (null renders disabled dummy UI).</param>
    public void DrawAnimationSection(IActor? actor)
    {
        bool enabled = actor != null;
        float opacity = enabled ? 1f : UIColors.DisabledOpacity;
        float buttonWidth = PoserButton.IconButtonSize * PoserUI.Scale;
        float spacing = 4f * PoserUI.Scale;

        // Clear state when switching actors
        if (actor != null && actor.Address != _lastActorAddress)
        {
            _selectedBaseId = null;
            _selectedBlendId = null;
            _appliedBaseId = null;
            _appliedBlendId = null;
            _lastActorAddress = actor.Address;
        }

        bool hasOverride = actor != null && _animationService.HasBaseOverride(actor);

        // Use selected ID for display, fall back to applied, then current from game
        var baseDisplayId = actor != null
            ? (_selectedBaseId ?? _appliedBaseId ?? _animationService.GetCurrentBaseAnimation(actor))
            : null;
        var blendDisplayId = _selectedBlendId ?? _appliedBlendId;

        using (PushOpacity(opacity))
        {
            // Current animation row - shows what's actually playing
            {
                using var row = PoserUI.Row(ImGui.GetTextLineHeight());
                row.Label("Current:", LabelWidth);
                row.Text(actor != null ? GetCurrentAnimationText(actor) : "None");
            }

            // Base Animation row - selector + play button
            {
                using var row = PoserUI.Row(PoserUI.DropdownHeight);
                row.Label("Base:", LabelWidth);

                // Calculate selector width: remaining - button - spacing
                float selectorWidth = row.RemainingWidth() - buttonWidth - spacing;
                row.Custom(selectorWidth / PoserUI.Scale, () =>
                {
                    _baseAnimationSelector.Draw("base_anim", baseDisplayId, id =>
                    {
                        if (enabled)
                        {
                            _selectedBaseId = id;
                        }
                    }, selectorWidth);
                });

                row.Spacer(spacing / PoserUI.Scale);

                // Play button - use baseDisplayId (what's shown in selector)
                float basePlayOpacity = baseDisplayId.HasValue ? 1f : UIColors.DisabledOpacity;
                using (PushOpacity(basePlayOpacity))
                {
                    if (row.IconButton("play_base", FontAwesomeIcon.Play, "Play Base Animation"))
                    {
                        if (enabled && actor != null && baseDisplayId.HasValue)
                        {
                            ushort? oldId = hasOverride ? _appliedBaseId : null;
                            _animationService.ApplyBaseAnimation(actor, baseDisplayId.Value, true);
                            _appliedBaseId = baseDisplayId.Value;
                            _animationService.SetAnimationTime(actor, 0f);

                            var action = new BaseAnimationAction(_animationService, actor, oldId, baseDisplayId.Value);
                            _historyService.Record(action);
                        }
                    }
                }
            }

            // Blend Animation row - selector + play button
            {
                using var row = PoserUI.Row(PoserUI.DropdownHeight);
                row.Label("Blend:", LabelWidth);

                // Calculate selector width: remaining - button - spacing
                float selectorWidth = row.RemainingWidth() - buttonWidth - spacing;
                row.Custom(selectorWidth / PoserUI.Scale, () =>
                {
                    _blendAnimationSelector.Draw("blend_anim", blendDisplayId, id =>
                    {
                        if (enabled)
                        {
                            _selectedBlendId = id;
                        }
                    }, selectorWidth);
                });

                row.Spacer(spacing / PoserUI.Scale);

                // Play button - use blendDisplayId (what's shown in selector)
                float blendPlayOpacity = blendDisplayId.HasValue ? 1f : UIColors.DisabledOpacity;
                using (PushOpacity(blendPlayOpacity))
                {
                    if (row.IconButton("play_blend", FontAwesomeIcon.Play, "Play Blend Animation"))
                    {
                        if (enabled && actor != null && blendDisplayId.HasValue)
                        {
                            _animationService.PlayBlendAnimation(actor, blendDisplayId.Value);
                            _appliedBlendId = blendDisplayId.Value;
                        }
                    }
                }
            }

            // Clear button row
            {
                float clearOpacity = hasOverride ? 1f : UIColors.DisabledOpacity;

                using var row = PoserUI.Row(PoserUI.ButtonHeight);
                row.Label("", LabelWidth); // Empty label for alignment

                // Clear Animations button - resets to original animation
                using (PushOpacity(clearOpacity))
                {
                    if (row.Button("clear_animations", "Reset"))
                    {
                        if (enabled && actor != null && hasOverride)
                        {
                            ushort? oldId = _appliedBaseId;
                            _animationService.StopBaseAnimation(actor);

                            var action = new BaseAnimationAction(_animationService, actor, oldId, null);
                            _historyService.Record(action);

                            _selectedBaseId = null;
                            _selectedBlendId = null;
                            _appliedBaseId = null;
                            _appliedBlendId = null;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets the display text for the current animation state.
    /// </summary>
    private string GetCurrentAnimationText(IActor actor)
    {
        var baseId = _animationService.GetCurrentBaseAnimation(actor);

        string baseName = "None";
        if (baseId.HasValue)
        {
            var entry = _animationDataService.GetById(baseId.Value);
            baseName = entry?.Name ?? $"#{baseId}";
        }

        // If we have a blend animation active, show both
        if (_appliedBlendId.HasValue)
        {
            var blendEntry = _animationDataService.GetById(_appliedBlendId.Value);
            string blendName = blendEntry?.Name ?? $"#{_appliedBlendId}";
            return $"{baseName} + {blendName}";
        }

        return baseName;
    }

    /// <summary>
    /// Draws the speed control section.
    /// </summary>
    /// <param name="actor">The actor to control (null renders disabled dummy UI).</param>
    public void DrawSpeedSection(IActor? actor)
    {
        bool enabled = actor != null;
        float opacity = enabled ? 1f : UIColors.DisabledOpacity;
        float speed = actor != null ? _animationService.GetSpeed(actor) : 1f;

        using (PushOpacity(opacity))
        {
            using var row = PoserUI.Row(PoserUI.ScrubberHeight);
            row.Label("Speed:", LabelWidth);

            row.CustomFill(width =>
            {
                if (Scrubber.Draw("##speed", ref speed, 0f, 3f, 0f, width, 1f, "F2", "x"))
                {
                    if (enabled && actor != null)
                    {
                        if (!_isEditingSpeed)
                        {
                            _speedBeforeEdit = _animationService.GetSpeed(actor);
                            _isEditingSpeed = true;
                        }
                        _animationService.SetSpeed(actor, speed);
                    }
                }

                if (_isEditingSpeed && !ImGui.IsItemActive() && actor != null)
                {
                    _isEditingSpeed = false;
                    if (MathF.Abs(_speedBeforeEdit - speed) > 0.001f)
                    {
                        var action = new SpeedChangeAction(_animationService, actor, _speedBeforeEdit, speed);
                        _historyService.Record(action);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Draws the time scrubbing section with playback controls.
    /// </summary>
    /// <param name="actor">The actor to control (null renders disabled dummy UI).</param>
    /// <param name="isFrozen">Whether the actor is frozen (required for scrubbing).</param>
    public void DrawScrubSection(IActor? actor, bool isFrozen)
    {
        bool enabled = actor != null;
        float? duration = actor != null ? _animationService.GetAnimationDuration(actor) : null;
        float? currentTime = actor != null ? _animationService.GetAnimationTime(actor) : null;

        float time = currentTime ?? 0f;
        float maxTime = duration ?? 1f;
        bool canScrub = enabled && isFrozen && duration.HasValue && currentTime.HasValue;

        float scrubOpacity = canScrub ? 1f : UIColors.DisabledOpacity;

        // Time scrubber row
        using (PushOpacity(scrubOpacity))
        {
            using var row = PoserUI.Row(PoserUI.ScrubberHeight);
            row.Label("Time:", LabelWidth);

            row.CustomFill(width =>
            {
                if (Scrubber.Draw("##time", ref time, 0f, maxTime, 0f, width, 1f, "F2", "s"))
                {
                    if (canScrub && actor != null)
                    {
                        // Clamp time to valid range
                        time = Math.Clamp(time, 0f, maxTime);
                        _animationService.SetAnimationTime(actor, time);
                    }
                }
            });
        }

        // Playback controls row (below time scrubber)
        float controlsOpacity = enabled ? 1f : UIColors.DisabledOpacity;
        float speed = actor != null ? _animationService.GetSpeed(actor) : 1f;
        bool isPlaying = speed > 0f;

        using (PushOpacity(controlsOpacity))
        {
            using var row = PoserUI.Row(PoserUI.ButtonHeight);
            row.Label("", LabelWidth); // Empty label for alignment

            // Play/Pause button - fixed width so it doesn't change size
            float playPauseWidth = ImGui.CalcTextSize("Pause").X + 24f * PoserUI.Scale; // "Pause" is wider
            row.Custom(playPauseWidth / PoserUI.Scale, () =>
            {
                if (PoserButton.DrawWithWidth("play_pause", isPlaying ? "Pause" : "Play", playPauseWidth))
                {
                    if (enabled && actor != null)
                    {
                        float oldSpeed = _animationService.GetSpeed(actor);
                        float newSpeed = isPlaying ? 0f : 1f;
                        _animationService.SetSpeed(actor, newSpeed);

                        var action = new SpeedChangeAction(_animationService, actor, oldSpeed, newSpeed);
                        _historyService.Record(action);
                    }
                }
            });

            row.Spacer(8);

            // Reset button
            if (row.Button("reset_speed", "Reset"))
            {
                if (enabled && actor != null)
                {
                    float oldSpeed = _animationService.GetSpeed(actor);
                    _animationService.ResetSpeed(actor);

                    var action = new SpeedChangeAction(_animationService, actor, oldSpeed, 1f);
                    _historyService.Record(action);
                }
            }
        }
    }

    /// <summary>
    /// Pushes ImGui style alpha for opacity-based disabled state.
    /// </summary>
    private static ImRaii.Style PushOpacity(float opacity)
    {
        return ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * opacity);
    }
}
