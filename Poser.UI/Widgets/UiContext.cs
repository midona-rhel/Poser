using System;
using System.Numerics;

namespace Poser.UI.Widgets;

/// <summary>
/// All mutable widget state in one place: the active theme, the text and
/// texture caches, interaction, menu, help and animation state, the host's
/// texture uploader and its log. The static widget classes draw through the
/// installed context. The host registers one context; <see cref="UIManager"/>
/// owns it and disposes it, which releases its textures and uninstalls it,
/// so a reload starts from fresh state with nothing for the host to reset.
/// </summary>
public sealed class UiContext : IDisposable
{
    /// <summary>The context the static widgets draw through: the last one
    /// constructed, until it is disposed.</summary>
    internal static UiContext Current { get; private set; } = null!;

    /// <param name="textureUploader">Uploads a baked RGBA8 image and
    /// returns its texture handle with the object that keeps it alive.</param>
    /// <param name="log">Diagnostics sink for the host's debug log.</param>
    public UiContext(
        Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)> textureUploader,
        Action<string> log)
    {
        Icons = new SvgIconTextureCache(textureUploader);
        Shadows = new BoxShadowTextureCache(textureUploader);
        Log = log;
        Current = this;
    }

    internal Action<string> Log { get; }
    internal SvgIconTextureCache Icons { get; }
    internal BoxShadowTextureCache Shadows { get; }

    internal readonly Themes.ThemeState Theme = new();
    internal readonly TextWidgets.TextState Text = new();
    internal readonly Interactive.InteractionState Interaction = new();
    internal readonly Motion.MotionState Animation = new();
    internal readonly GlassChrome.GlassState Glass = new();
    internal readonly FloatingMenu.MenuState Menu = new();
    internal readonly HoverHelp.HelpState Help = new();
    internal readonly DialogWidgets.DialogState Dialogs = new();
    internal readonly PageForm.SectionState Sections = new();
    internal readonly AxisWellWidgets.AxisEditState AxisEdit = new();
    internal readonly TextInputWidgets.RefocusState Refocus = new();
    internal readonly WindowMovement.TitleDragState TitleDrag = new();

    /// <summary>Backs <see cref="ButtonWidgets.ButtonSeat"/>.</summary>
    internal Vector2 ButtonSeat;

    /// <summary>
    /// Raised when a slider, well or field commits: the drag released, the
    /// typed value accepted on leaving the field. The value journal seals
    /// its open step here, so a drag is one undo step from press to
    /// release, a typed edit is one step from focus to unfocus, and the
    /// next touch of the same control opens a new step (ruled 2026-09-03).
    /// </summary>
    public event Action<object>? ValueCommitted;
    public event Action<object>? ValueEditBegan;
    public event Action? ValueEditEnded;
    public event Action? ValueEditingIdle;

    internal void RaiseValueCommitted(object id) => ValueCommitted?.Invoke(id);
    internal void RaiseValueEditBegan(object id) => ValueEditBegan?.Invoke(id);
    internal void RaiseValueEditEnded() => ValueEditEnded?.Invoke();
    internal void RaiseValueEditingIdle() => ValueEditingIdle?.Invoke();

    /// <summary>Releases the textures this context uploaded and uninstalls it.</summary>
    public void Dispose()
    {
        Icons.Clear();
        Shadows.Clear();
        if (Current == this)
            Current = null!;
    }
}
