using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Settings;

namespace Poser.Game.Cameras;

/// <summary>
/// Lets GPose perform its forced depth-of-field reset, then disables depth of
/// field through the same unforced call used by the GPose menu. Forced calls
/// from the idle camera and all unforced calls pass through unchanged.
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
                // Without this check, a forced idle-camera call cannot be distinguished from GPose entry.
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
