using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Poser.Game.Diagnostics;

/// <summary>Read-only breadcrumbs for native model lifetime across GPose boundaries.</summary>
internal static partial class GPoseTransitionLog
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    private static unsafe bool Read<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        fixed (T* result = &value)
            return address != 0 && ReadProcessMemory(-1, address, result, (nuint)sizeof(T), out var read) && read == (nuint)sizeof(T);
    }

    internal static void Snapshot(IPluginLog log, string phase, IObjectTable? objects)
    {
        log.Information($"[GPoseLifetime] {phase}");
        if (objects == null) return;
        try
        {
            foreach (var actor in objects)
                if (actor is Dalamud.Game.ClientState.Objects.Types.ICharacter &&
                    (actor.ObjectIndex == 0 || actor.ObjectIndex is >= 200 and <= 439 ||
                     actor.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion or
                        Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Mount or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Ornament))
                    Actor(log, phase, actor.Address, $"index={actor.ObjectIndex} kind={actor.ObjectKind} name={actor.Name}");
        }
        catch (Exception ex) { log.Warning($"[GPoseLifetime] {phase} snapshot unavailable: {ex.Message}"); }
    }

    internal static unsafe void Actor(IPluginLog? log, string phase, nint address, string label)
    {
        if (log == null) return;
        try
        {
            var actor = (Character*)address;
            bool gotDraw = Read((nint)(&((GameObject*)address)->DrawObject), out nint draw);
            bool gotModel = Read((nint)(&actor->ModelContainer.ModelCharaId), out int model);
            log.Information($"[GPoseLifetime] {phase} {label.Replace('\r', ' ').Replace('\n', ' ')} actor=0x{address:X} model={(gotModel ? model.ToString() : "unreadable")} draw={(gotDraw ? $"0x{draw:X}" : "unreadable")}");
            if (gotDraw && draw != 0) Model(log, phase, draw);
        }
        catch (Exception ex) { log.Warning($"[GPoseLifetime] {phase} actor=0x{address:X} unavailable: {ex.Message}"); }
    }

    internal static unsafe void Model(IPluginLog log, string phase, nint draw)
    {
        try { ReadModel(log, phase, draw); }
        catch (Exception ex) { log.Warning($"[GPoseLifetime] {phase} draw=0x{draw:X} unavailable: {ex.Message}"); }
    }

    private static unsafe void ReadModel(IPluginLog log, string phase, nint draw)
    {
        // A crash investigation must not dereference the suspect owner/parent
        // chain. RPM reports unreadable pages without faulting the game again.
        var character = (CharacterBase*)draw;
        bool gotSkeleton = Read((nint)(&character->Skeleton), out nint skeleton);
        nint owner = 0;
        bool gotOwner = gotSkeleton && skeleton != 0 && Read((nint)(&((FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton*)skeleton)->Owner), out owner);
        var parents = new List<string>();
        nint current = draw;
        var seen = new HashSet<nint> { draw };
        for (int depth = 0; depth < 8; depth++)
        {
            if (!Read((nint)(&((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)current)->ParentObject), out nint parent))
            { parents.Add("unreadable"); break; }
            parents.Add($"0x{parent:X}");
            if (parent == 0) break;
            if (!seen.Add(parent)) { parents.Add("cycle"); break; }
            current = parent;
            if (depth == 7) parents.Add("truncated");
        }
        log.Information($"[GPoseLifetime] {phase} draw=0x{draw:X} skeleton={(gotSkeleton ? $"0x{skeleton:X}" : "unreadable")} owner={(gotOwner ? $"0x{owner:X}" : "unreadable")} parents={string.Join(" -> ", parents)}");
    }
}
