using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public record struct PopoverProps
{
    /// <summary>Unscaled width. The popover never sizes to content —
    /// a search result list would resize under the pointer as the user
    /// types, which moves the rows they are aiming at.</summary>
    public float Width;
    /// <summary>Unscaled height.</summary>
    public float Height;
    /// <summary>Screen rect the popover anchors under, in pixels. It
    /// flips above when there is no room below.</summary>
    public Vector2 AnchorMin;
    public Vector2 AnchorMax;
    /// <summary>Unscaled inner padding. Default 8.</summary>
    public float Padding;
}

public static partial class LegacyCrystarium
{
    /// <summary>
    /// Opens the popover, dropdown, colour picker or modal registered
    /// under <paramref name="id"/>. This is the ONE open path: it claims
    /// the exclusive chain before ImGui's popup stack, so the surface
    /// owns input — and occludes what is under it — from the frame it
    /// opens. Calling <c>ImGui.OpenPopup</c> directly skips that claim
    /// and leaves the surface unable to occlude anything.
    /// </summary>
    public static void OpenPopover(string id) => FloatingSurface.OpenPopup(id);

    /// <summary>
    /// Anchored glass popover with a caller-supplied body — the shared
    /// shell behind pickers and any other "click a control, get a panel"
    /// surface.
    ///
    /// It is the same glass recipe as ContextMenu, Modal and ColorWell
    /// (backdrop blur, the border trio, radius 8), which those three each
    /// duplicated inline; this is the one place it now lives. Unlike
    /// ContextMenu it is a fixed size and scrolls, and unlike Modal it
    /// does not block input behind it.
    ///
    /// Open it with <see cref="OpenPopover(string)"/> — never
    /// <c>ImGui.OpenPopup</c> directly, which would skip the
    /// exclusive-chain claim and leave the popover unable to occlude what
    /// is under it. Returns true while it is open, after invoking
    /// <paramref name="body"/>.
    /// </summary>
    public static bool Popover(string id, in PopoverProps props, Action body)
        => FloatingSurface.Popup(
            id,
            new FloatingSurfaceProps
            {
                Width = props.Width,
                Height = props.Height,
                Padding = props.Padding > 0f
                    ? props.Padding
                    : Crystarium.ActiveTheme.Floating.PopoverPadding,
                AnchorMin = props.AnchorMin,
                AnchorMax = props.AnchorMax,
            },
            body);

    public static bool Popover(
        string id,
        in PopoverProps props,
        Action<PopoverScope> body)
    {
        float padding = props.Padding > 0f
            ? props.Padding
            : ActiveTheme.Floating.PopoverPadding;
        float contentWidth =
            MathF.Max(0f, props.Width - padding * 2f);
        float contentHeight =
            MathF.Max(0f, props.Height - padding * 2f);
        return FloatingSurface.Popup(
            id,
            new FloatingSurfaceProps
            {
                Width = props.Width,
                Height = props.Height,
                Padding = padding,
                AnchorMin = props.AnchorMin,
                AnchorMax = props.AnchorMax,
            },
            () => body(new PopoverScope(
                ImGui.GetCursorScreenPos(),
                contentWidth,
                contentHeight,
                ImGuiHelpers.GlobalScale)));
    }

    public sealed class PopoverScope
    {
        private readonly Vector2 _origin;
        private readonly float _width;
        private readonly float _height;
        private readonly float _scale;
        private float _y;

        internal PopoverScope(
            Vector2 origin,
            float width,
            float height,
            float scale)
        {
            _origin = origin;
            _width = width;
            _height = height;
            _scale = scale;
        }

        public void Caption(string text)
        {
            MoveToCurrent();
            LabelInBand(
                ImGui.GetCursorScreenPos(),
                new Vector2(
                    _width,
                    ActiveTheme.Page.StatusLineHeight) * _scale,
                text,
                new TextStyle
                {
                    Size = ActiveTheme.Typography.CaptionSize,
                    Weight = FontWeight.Medium,
                    Color = FormLabelColor,
                });
            Advance(
                ActiveTheme.Page.StatusLineHeight
                + ActiveTheme.Spacing.Two);
        }

        public void Filter(
            string id,
            string value,
            Action<string> onChange,
            string placeholder)
        {
            MoveToCurrent();
            FilterPill(
                id,
                value,
                onChange,
                placeholder,
                ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(_width),
                });
            Advance(
                ActiveTheme.Controls.WorkspaceHeight
                + ActiveTheme.Spacing.Two);
        }

        public void Segmented(
            string id,
            string[] items,
            int selected,
            Action<int> onChange)
        {
            MoveToCurrent();
            SegmentedControl(
                id,
                items,
                selected,
                onChange,
                ControlStyle.Workspace with
                {
                    Width = UiWidth.Fixed(_width),
                });
            Advance(
                ActiveTheme.Controls.NavigationHeight
                + ActiveTheme.Spacing.Two);
        }

        public void List(
            string id,
            Action<ScrollRegionScope> content)
        {
            MoveToCurrent();
            ScrollRegion(
                id,
                _width,
                MathF.Max(0f, _height - _y),
                content);
            _y = _height;
        }

        private void MoveToCurrent() =>
            ImGui.SetCursorScreenPos(
                _origin + new Vector2(0f, _y * _scale));

        private void Advance(float amount) => _y += amount;
    }
}
