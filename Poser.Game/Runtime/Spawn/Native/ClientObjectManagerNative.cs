using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Poser.Game;

/// <summary>The live ClientObjectManager. The only place a client object's
/// global <c>ObjectIndex</c> is read.</summary>
internal unsafe sealed class ClientObjectManagerNative : IClientObjectManagerNative
{
    public bool IsAvailable => ClientObjectManager.Instance() is not null;

    public uint CreateBattleCharacter(uint index, byte param)
    {
        var com = ClientObjectManager.Instance();
        return com is null
            ? SpawnClientObjects.NoIndex
            : com->CreateBattleCharacter(index, param);
    }

    public ClientObjectSnapshot? GetObjectByIndex(ushort index)
    {
        var com = ClientObjectManager.Instance();
        if (com is null)
            return null;
        var native = com->GetObjectByIndex(index);
        if (native is null)
            return null;
        return new ClientObjectSnapshot(
            (nint)native, native->EntityId, native->ObjectIndex);
    }

    public uint GetIndexByObject(nint address)
    {
        var com = ClientObjectManager.Instance();
        if (com is null || address == nint.Zero)
            return SpawnClientObjects.NoIndex;
        return com->GetIndexByObject((GameObject*)address);
    }

    public void DeleteObjectByIndex(ushort index, byte param)
    {
        var com = ClientObjectManager.Instance();
        if (com is not null)
            com->DeleteObjectByIndex(index, param);
    }
}
