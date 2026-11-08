using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Settings;

namespace Poser.Game.Input;

public sealed class GPoseMouseTargetHook : IDisposable
{
    private delegate nint MouseTargetDelegate(nint a1, nint a2, nint a3);

    private readonly Hook<MouseTargetDelegate> _hook;
    private readonly IClientState _clientState;
    private readonly ConfigurationService _config;

    public GPoseMouseTargetHook(
        ISigScanner scanner,
        IGameInteropProvider interop,
        IClientState clientState,
        ConfigurationService config)
    {
        _clientState = clientState;
        _config = config;
        // Returning zero suppresses native mouse targeting. Camera input is
        // handled elsewhere and is not consumed by this detour.
        const string signature =
            Poser.Game.Natives.NativeSignatures.TargetSystem_ProcessMouseState;
        _hook = interop.HookFromAddress<MouseTargetDelegate>(
            scanner.ScanText(signature), Detour);
        _hook.Enable();
    }

    private nint Detour(nint a1, nint a2, nint a3) =>
        _clientState.IsGPosing && _config.Config.BlockGPoseMouseTargeting
            ? 0
            : _hook.Original(a1, a2, a3);

    public void Dispose() => _hook.Dispose();
}
