using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Settings;

namespace Poser.Game.Cameras;

/// <summary>
/// Entering GPose switches depth of field on, whatever the System Config says:
/// GPose start calls GPoseEnv.SetDepthOfField(env, remembered, force 1). With
/// "Start GPose without depth of field" that call still runs as the game made it
/// (its forced call also resets the DoF parameters), and DoF is then switched off
/// the way GPose's own menu toggle does it (unforced). The idle camera forces it
/// on too and is left alone, as is every unforced call. Never throws into the
/// game.
/// </summary>
public sealed class GPoseDepthOfFieldGuard : IDisposable
{
    private delegate void SetDepthOfFieldDelegate(nint env, byte enable, byte force);
    // EventIdleCamController.IsActive ignores its argument.
    private delegate byte IdleCameraActiveDelegate(nint self);

    private readonly Hook<SetDepthOfFieldDelegate>? _hook;
    private readonly IdleCameraActiveDelegate? _idleCameraActive;
    private readonly ConfigurationService _configuration;

    public GPoseDepthOfFieldGuard(ISigScanner scanner, IGameInteropProvider hooking, IPluginLog log,
        ConfigurationService configuration)
    {
        _configuration = configuration;
        try
        {
            if (!scanner.TryScanText(Poser.Game.Natives.NativeSignatures.EventIdleCamController_IsActive, out var idle)
                || idle == 0)
            {
                // Without it the idle camera couldn't be told apart: leave DoF to the game.
                log.Warning("GPose depth of field: the idle camera check was not found; GPose keeps turning it on at entry.");
                return;
            }
            _idleCameraActive = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<IdleCameraActiveDelegate>(idle);
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
        _hook!.Original(env, enable, force);
        try
        {
            if (force != 0 && enable != 0 && _configuration.Config.StartGPoseWithoutDepthOfField
                && _idleCameraActive!(env) == 0)
                _hook.Original(env, 0, 0);
        }
        catch
        {
            // The game's own call already ran; nothing else changes.
        }
    }

    public void Dispose() => _hook?.Dispose();
}
