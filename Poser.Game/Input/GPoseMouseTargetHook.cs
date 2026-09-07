using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Config;

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
        // Brio's GPose mouse-target handler: returning zero prevents the
        // native hit from becoming a target, without consuming camera input.
        const string signature =
            "40 57 48 83 EC ?? 48 89 5C 24 ?? 48 8B F9 48 89 6C 24 ?? 48 89 74 24 ?? 49 8B F0";
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
