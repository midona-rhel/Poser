using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Internal core renderer for <see cref="Norvrandt.Element"/>.
///
/// <para>Element is split across files via <c>partial class</c>:</para>
/// <list type="bullet">
///   <item><c>Layout/LayoutContext.cs</c> — thread-static row / grid / position stacks</item>
///   <item><c>Layout/Row.cs</c> — flex row container + child layout (<c>RunRowChildren</c>, AlignSelf, wrap, Auto measure)</item>
///   <item><c>Layout/Column.cs</c> — block / flex-column container (chrome via channels, overflow, BeginChild)</item>
///   <item><c>Layout/Grid.cs</c> — CSS Grid (template columns, spans, auto-flow)</item>
///   <item><c>Layout/Absolute.cs</c> — Position.Absolute / Position.Fixed</item>
///   <item><c>Layout/Inline.cs</c> — row / grid child render path (cell rect provided by parent)</item>
/// </list>
///
/// <para>This file owns the public dispatcher (<see cref="Render"/>) and the
/// helpers shared by every layout: <c>DrawChrome</c>, <c>PushCascade</c>,
/// <c>PopCascade</c>, <c>ApplyCursor</c>, <c>ApplyOverflowClip</c>,
/// <c>ResolveOuterWidth</c>, <c>ApplyMinMax{Width,Height}</c>.</para>
/// </summary>
internal static partial class Element
{
    public static void Render(ElementProps props, Action? children)
    {
        Stylesheet.EnsureInitialized();

        // Process any registered hotkeys once per frame. Idempotent; no cost on subsequent calls.
        Hotkey.ProcessFrame();

        var state = props.Disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.Resolve(props.Classes, props.Id, state).MergedWith(props.Style);

        if (resolved.Display == UI.Display.None) return;
        if (resolved.Transition.HasValue)
            resolved = Animator.Step(props.Id, resolved, resolved.Transition.Value);

        var pos = resolved.Position ?? UI.Position.Static;
        if (pos == UI.Position.Absolute || pos == UI.Position.Fixed)
        {
            RenderPositioned(props, children, resolved, pos);
            return;
        }

        // Grid mode: explicit Display.Grid OR a non-null GridTemplateColumns.
        if ((resolved.Display ?? Display.Block) == Display.Grid || resolved.GridTemplateColumns != null)
        {
            if (resolved.GridTemplateColumns != null)
            {
                RenderGrid(props, children, resolved);
                return;
            }
        }

        // Row / grid child registration.
        if (_rowStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            _rowStack.Peek().Items.Add(new RowItem
            {
                Width = resolved.Width ?? Sizing.Fill,
                Height = resolved.Height,
                AlignSelf = resolved.AlignSelf,
                Render = (w, h) => RenderInline(capProps, capChildren, w, h),
            });
            return;
        }

        if (_gridStack is { Count: > 0 })
        {
            var capProps = props;
            var capChildren = children;
            _gridStack.Peek().Items.Add(new GridItem
            {
                GridColumn = resolved.GridColumn,
                GridRow = resolved.GridRow,
                ColumnSpan = resolved.GridColumnSpan ?? 1,
                RowSpan = resolved.GridRowSpan ?? 1,
                Height = resolved.Height,
                Render = (w, h) => RenderInline(capProps, capChildren, w, h),
            });
            return;
        }

        // Top-level: own row / column / grid container.
        var width   = resolved.Width   ?? Sizing.Fill;
        var height  = resolved.Height;
        var direction = resolved.FlexDirection ?? FlexDirection.Column;
        var padding = resolved.Padding ?? new Spacing(0);
        var margin  = resolved.Margin  ?? new Spacing(0);
        var gap     = resolved.Gap     ?? 0f;

        float scale = ImGuiHelpers.GlobalScale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = ResolveOuterWidth(width, availWidth - margin.Horizontal * scale, scale);
        outerWidth = ApplyMinMaxWidth(outerWidth, resolved, scale);

        if (resolved.AspectRatio.HasValue && resolved.AspectRatio.Value > 0f
            && !(height.HasValue && height.Value.Mode == SizingMode.Fixed))
        {
            float ratioHeight = outerWidth / resolved.AspectRatio.Value;
            height = Sizing.Fixed(ratioHeight / scale);
        }

        if (direction == FlexDirection.Row)
        {
            float outerHeight = (height.HasValue && height.Value.Mode == SizingMode.Fixed)
                ? height.Value.Value * scale
                : 24f * scale;
            outerHeight = ApplyMinMaxHeight(outerHeight, resolved, scale);
            RenderRow(props, children, resolved, outerWidth, outerHeight, margin, padding, gap, resolved.AlignItems, resolved.FlexWrap, resolved.RowGap ?? 4f);
        }
        else
        {
            RenderColumn(props, children, resolved, outerWidth, margin, padding);
        }
    }

    // ---- Shared chrome / cascade / cursor / clip helpers ----

    private static void DrawChrome(Vector2 min, Vector2 max, in ElementStyle resolved)
    {
        bool hasChrome = resolved.BackgroundColor.HasValue
                      || resolved.BackgroundGradient.HasValue
                      || resolved.BackgroundImage != null
                      || resolved.BackgroundSvg != null
                      || (resolved.BorderWidth ?? 0f) > 0f
                      || resolved.BoxShadow.HasValue
                      || (resolved.BoxShadows != null && resolved.BoxShadows.Length > 0)
                      || resolved.Outline.HasValue;
        if (!hasChrome) return;
        var box = new BoxStyle
        {
            BackgroundColor = resolved.BackgroundColor,
            BackgroundGradient = resolved.BackgroundGradient,
            BackgroundImage = resolved.BackgroundImage,
            BackgroundImageFit = resolved.BackgroundImageFit,
            BackgroundSvg = resolved.BackgroundSvg,
            BorderColor = resolved.BorderColor,
            BorderTopColor = resolved.BorderTopColor,
            BorderRightColor = resolved.BorderRightColor,
            BorderBottomColor = resolved.BorderBottomColor,
            BorderLeftColor = resolved.BorderLeftColor,
            BorderWidth = resolved.BorderWidth ?? 0f,
            BorderRadius = resolved.BorderRadius ?? 0f,
            BoxShadow = resolved.BoxShadow,
            BoxShadows = resolved.BoxShadows,
            Outline = resolved.Outline,
            RaisedGradient = resolved.RaisedGradient ?? false,
        };
        BoxRenderer.Draw(ImGui.GetWindowDrawList(), min, max, box);
    }

    private static int PushCascade(in ElementStyle resolved)
    {
        int colorPushes = 0;
        if (resolved.Color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, resolved.Color.Value);
            colorPushes++;
        }
        if (resolved.Opacity.HasValue)
        {
            float current = ImGui.GetStyle().Alpha;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, current * resolved.Opacity.Value);
        }

        // Font selection: if FontSize or FontWeight is set, resolve a sized handle via
        // FontRegistry. Otherwise fall back to the family default (Mono/Icon/Default).
        bool fontPushed = false;
        var family = resolved.FontFamily ?? FontFamily.Default;
        if ((resolved.FontSize.HasValue || resolved.FontWeight.HasValue) && family != FontFamily.Icon)
        {
            var handle = FontRegistry.Resolve(
                family,
                resolved.FontWeight ?? FontWeight.Regular,
                resolved.FontSize ?? Crystarium.ActiveTheme.Typography.BodySize);
            if (handle is { Available: true })
            {
                handle.Push();
                _pushedHandles.Push(handle);
                fontPushed = true;
            }
        }
        if (!fontPushed && resolved.FontFamily.HasValue && family != FontFamily.Default)
        {
            var font = family switch
            {
                FontFamily.Mono => UiBuilder.MonoFont,
                FontFamily.Icon => UiBuilder.IconFont,
                _ => UiBuilder.DefaultFont,
            };
            ImGui.PushFont(font);
            _pushedHandles.Push(null); // raw ImGui push — popped with PopFont
            fontPushed = true;
        }
        return Pack(colorPushes, resolved.Opacity.HasValue, fontPushed);
    }

    // IFontHandle.Push tracks a handle-private stack — popping it with raw
    // ImGui.PopFont() leaves that stack unbalanced (warn-spam every frame,
    // native crash under heavy text). Pop the SAME WAY we pushed.
    private static readonly System.Collections.Generic.Stack<Dalamud.Interface.ManagedFontAtlas.IFontHandle?> _pushedHandles = new();

    private static void PopCascade(int packed)
    {
        var (colors, alpha, font) = Unpack(packed);
        if (font)
        {
            var handle = _pushedHandles.Count > 0 ? _pushedHandles.Pop() : null;
            if (handle != null) handle.Pop();
            else ImGui.PopFont();
        }
        if (alpha) ImGui.PopStyleVar();
        if (colors > 0) ImGui.PopStyleColor(colors);
    }

    private static int Pack(int colors, bool alpha, bool font)
        => colors | (alpha ? 1 << 8 : 0) | (font ? 1 << 9 : 0);
    private static (int, bool, bool) Unpack(int p) => (p & 0xFF, (p & (1 << 8)) != 0, (p & (1 << 9)) != 0);

    private static bool ApplyOverflowClip(in ElementStyle resolved, Vector2 min, Vector2 max)
    {
        if ((resolved.Overflow ?? UI.Overflow.Visible) != UI.Overflow.Hidden) return false;
        ImGui.GetWindowDrawList().PushClipRect(min, max, true);
        return true;
    }

    private static void ApplyCursor(Cursor c)
    {
        var imc = c switch
        {
            UI.Cursor.Pointer    => ImGuiMouseCursor.Hand,
            UI.Cursor.Hand       => ImGuiMouseCursor.Hand,
            UI.Cursor.TextInput  => ImGuiMouseCursor.TextInput,
            UI.Cursor.ResizeNS   => ImGuiMouseCursor.ResizeNs,
            UI.Cursor.ResizeEW   => ImGuiMouseCursor.ResizeEw,
            UI.Cursor.ResizeAll  => ImGuiMouseCursor.ResizeAll,
            UI.Cursor.NotAllowed => ImGuiMouseCursor.NotAllowed,
            _ => ImGuiMouseCursor.Arrow,
        };
        ImGui.SetMouseCursor(imc);
    }

    private static float ResolveOuterWidth(Sizing width, float availableWidth, float scale)
    {
        return width.Mode switch
        {
            SizingMode.Fixed => width.Value * scale,
            SizingMode.Fill  => availableWidth,
            SizingMode.Auto  => availableWidth,
            _ => availableWidth,
        };
    }

    private static float ApplyMinMaxWidth(float width, in ElementStyle resolved, float scale)
    {
        if (resolved.MinWidth.HasValue && resolved.MinWidth.Value.Mode == SizingMode.Fixed)
            width = MathF.Max(width, resolved.MinWidth.Value.Value * scale);
        if (resolved.MaxWidth.HasValue && resolved.MaxWidth.Value.Mode == SizingMode.Fixed)
            width = MathF.Min(width, resolved.MaxWidth.Value.Value * scale);
        return width;
    }

    private static float ApplyMinMaxHeight(float height, in ElementStyle resolved, float scale)
    {
        if (resolved.MinHeight.HasValue && resolved.MinHeight.Value.Mode == SizingMode.Fixed)
            height = MathF.Max(height, resolved.MinHeight.Value.Value * scale);
        if (resolved.MaxHeight.HasValue && resolved.MaxHeight.Value.Mode == SizingMode.Fixed)
            height = MathF.Min(height, resolved.MaxHeight.Value.Value * scale);
        return height;
    }
}
