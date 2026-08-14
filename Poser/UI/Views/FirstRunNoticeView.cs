using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;

namespace Poser.UI.Views;

/// <summary>
/// The first-run acceptance gate. Drawn by the shell as an undismissable
/// Crystarium modal: ImGui's modal layer dims and blocks every other window in
/// the context, so the workspace behind it — attached or detached, plus the
/// part windows, settings and pop-outs — is visible and inert until the notice
/// is accepted.
///
/// <para>The gate holds no plugin state and owns no resources: it reads the
/// live config through <see cref="ConfigurationService.Instance"/> and writes
/// exactly one integer, so teardown has nothing to unwind here.</para>
/// </summary>
public sealed class FirstRunNoticeView
{
    /// <summary>Sized to hold the whole notice without scrolling at the design
    /// scale: the body's paragraphs measure ~370px at the Large width, and the
    /// two 44px bars sit outside that.</summary>
    private const float DialogHeight = 500f;
    private const float ParagraphGap = 10f;
    private const float ConfirmationFieldWidth = 180f;

    private string _typed = string.Empty;

    /// <summary>Opens the browser on a credited project. Assigned by the host
    /// so the view keeps no Dalamud dependency of its own.</summary>
    public Action<string>? OnOpenUrl;

    /// <summary>True while the workspace is gated. The host suppresses
    /// workspace input paths that do not travel through ImGui (keybinds) while
    /// this holds.</summary>
    public static bool Pending =>
        !FirstRunNotice.IsAccepted(ConfigurationService.Instance.Config);

    public void Draw()
    {
        if (!Pending)
            return;

        Crystarium.Modal(
            "##first-run-notice",
            true,
            // The gate closes by acceptance alone. ImGui's own dismissals
            // (Escape, a click outside) reach this and are answered by the
            // next frame reopening the modal, so there is nothing to record.
            _ => { },
            "Before you use Poser",
            DrawBody,
            DrawFooter,
            ModalSize.Large,
            DialogHeight,
            dismissible: false);
    }

    private void DrawBody()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = ImGui.GetContentRegionAvail().X;
        var theme = Crystarium.ActiveTheme;

        Paragraph(
            "Poser was coded with the use of artificial intelligence.",
            width,
            new TextStyle { Weight = FontWeight.Medium, Color = theme.Text });

        Paragraph(
            "It is derivative of, and heavily inspired by, Anamnesis, Ktisis "
                + "and Brio. Those three did all of this first; Poser stands on "
                + "the shoulders of the giants who coded them.",
            width,
            default);

        // The repository row: one button per credited project, in the order
        // they arrived — Anamnesis, then Ktisis, then Brio.
        for (int i = 0; i < FirstRunNotice.Upstream.Length; i++)
        {
            var project = FirstRunNotice.Upstream[i];
            if (i > 0)
                ImGui.SameLine(0f, 8f * scale);
            Crystarium.Button(
                project.Name,
                () => OnOpenUrl?.Invoke(project.Url),
                id: $"notice-link-{project.Name}",
                help: project.Url);
        }
        ImGui.Dummy(new Vector2(0f, ParagraphGap * scale));

        foreach (var project in FirstRunNotice.Upstream)
            Paragraph(
                $"{project.Name} — {project.Credit}.",
                width,
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.TextDim,
                },
                gap: 2f);
        ImGui.Dummy(new Vector2(0f, (ParagraphGap - 2f) * scale));

        Paragraph(
            "If you are not comfortable using AI-generated code, uninstall "
                + "this plugin and use those projects instead.",
            width,
            default);

        Paragraph(
            "This is a beta — a first release candidate. It is not stable and "
                + "it is not finished. If that is not to your taste, the three "
                + "projects above are the mature alternatives.",
            width,
            default);

        Paragraph(
            $"Type \"{FirstRunNotice.ConfirmationPhrase}\" below to confirm you "
                + "have read and understood this.",
            width,
            new TextStyle { Weight = FontWeight.Medium, Color = theme.Text },
            gap: 0f);
    }

    private void DrawFooter()
    {
        Crystarium.TextInput(
            "##first-run-accept",
            _typed,
            next => _typed = next,
            ControlStyle.Comfortable with
            {
                Width = UiWidth.Fixed(ConfirmationFieldWidth),
            },
            placeholder: FirstRunNotice.ConfirmationPhrase);
        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        Crystarium.Button(
            "Accept",
            Accept,
            ButtonVariant.Primary,
            ControlStyle.Comfortable,
            disabled: !FirstRunNotice.Confirms(_typed),
            id: "first-run-accept-button");
    }

    private void Accept()
    {
        var configuration = ConfigurationService.Instance;
        FirstRunNotice.Accept(configuration.Config);
        configuration.Save();
        _typed = string.Empty;
    }

    private static void Paragraph(
        string text,
        float width,
        in TextStyle style,
        float gap = ParagraphGap)
    {
        Crystarium.Text(text, style, TextConstraint.Wrap(width));
        if (gap > 0f)
            ImGui.Dummy(new Vector2(0f, gap * ImGuiHelpers.GlobalScale));
    }
}
