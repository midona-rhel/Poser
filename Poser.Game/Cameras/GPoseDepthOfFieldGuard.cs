using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Settings;

namespace Poser.Game.Cameras;

/// <summary>
/// Entering GPose switches depth of field on, whatever the System Config says:
/// GPose start calls GPoseEnv.SetDepthOfField(env, remembered, force 1). With
/// "Start GPose without depth of field" that one forced call passes off instead.
/// GPose's own DoF toggle calls it unforced and is left alone. Never throws into
/// the game: a failed check passes the call through untouched.
/// </summary>
public sealed class GPoseDepthOfFieldGuard : IDisposable
{
    private delegate void SetDepthOfFieldDelegate(nint env, byte enable, byte force);

    private readonly Hook<SetDepthOfFieldDelegate>? _hook;
    private readonly ConfigurationService _configuration;

    public GPoseDepthOfFieldGuard(ISigScanner scanner, IGameInteropProvider hooking, IPluginLog log,
        ConfigurationService configuration)
    {
        _configuration = configuration;
        try
        {
            if (scanner.TryScanText(Poser.Game.Natives.NativeSignatures.GPoseEnv_SetDepthOfField, out var address)
                && address != 0)
            {
                _hook = hooking.HookFromAddress<SetDepthOfFieldDelegate>(address, SetDepthOfFieldDetour);
                _hook.Enable();
            }
            else
                log.Warning("GPose depth of field: the function was not found; GPose keeps turning it on at entry.");
        }
        catch (Exception ex)
        {
            log.Warning($"GPose depth of field: the hook could not be created ({ex.Message}).");
        }
    }

    private void SetDepthOfFieldDetour(nint env, byte enable, byte force)
    {
        try
        {
            if (force != 0 && _configuration.Config.Camera.StartGPoseWithoutDepthOfField)
                enable = 0;
        }
        catch
        {
            // The setting couldn't be read: the game's own value goes through.
        }
        _hook!.Original(env, enable, force);
    }

    public void Dispose() => _hook?.Dispose();
}
