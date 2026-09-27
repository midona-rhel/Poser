using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Identity;

namespace Poser.UI;

public sealed class IdleModExportDialog(IIdleModExport export, UserNotices notices, IFramework framework)
{
    private readonly Crystarium.FileDialog _dialog =
        new("Export experimental standing pose 1 mod", new[] { ".pmp" }, isSaveMode: true);

    public bool Busy => export.Busy;
    public void Draw() => _dialog.Draw();
    public void Open(ActorId actor, Controls.RememberedFolder folder)
    {
        notices.Note("Experimental: replaces standing /cpose 1, including the face. Import into this character's Penumbra collection only, with the same face and Customize+ profile.");
        folder.Open(_dialog, path => _ = Save(actor, path));
    }

    private async Task Save(ActorId actor, string path)
    {
        try
        {
            await export.ExportAsync(actor, path);
            await framework.RunOnFrameworkThread(() => notices.Done($"Idle mod saved to {path}. Import the PMP in Penumbra."));
        }
        catch (Exception ex)
        {
            await framework.RunOnFrameworkThread(() => notices.Failed("Idle export", ex.Message));
        }
    }
}
