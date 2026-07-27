using System;
using Dalamud.Plugin;
using Poser.Config;
using Poser.UI;

namespace Poser;

/// <summary>
/// Builds a Crystarium <see cref="Theme"/> from Poser's <see cref="UIConfiguration"/>
/// and re-installs the default stylesheet whenever the user edits colors. This
/// is the only place that knows both Crystarium internals and Poser config —
/// keeps Crystarium reusable.
///
/// <para><b>Sync timing:</b> the first sync is deferred to the first draw frame
/// because <see cref="UIColorEntry.Resolve"/> reads
/// <c>ImGui.GetStyle().Colors</c> when the entry is in non-custom mode, and
/// that is unsafe outside an ImGui frame.</para>
/// </summary>
internal static class ThemeBridge
{
    private static ConfigurationService? _service;
    private static IDalamudPluginInterface? _pi;

    public static void Initialize(IDalamudPluginInterface pi, ConfigurationService service)
    {
        _service = service;
        _pi = pi;

        pi.UiBuilder.Draw += OneShotApply;
        service.OnConfigurationChanged += Apply;
    }

    public static void Dispose()
    {
        if (_pi != null)
            _pi.UiBuilder.Draw -= OneShotApply;
        if (_service != null)
            _service.OnConfigurationChanged -= Apply;
        _service = null;
        _pi = null;
    }

    private static void OneShotApply()
    {
        if (_pi != null)
            _pi.UiBuilder.Draw -= OneShotApply;
        Apply();
    }

    private static void Apply()
    {
        // The retained UI owns its surface, text, and border tokens. Only the
        // accent choice survives from legacy configuration.
        Crystarium.UseTheme(Theme.PictoDark);
    }
}
