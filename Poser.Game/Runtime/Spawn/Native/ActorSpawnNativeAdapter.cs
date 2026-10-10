using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Domain.Companions;

namespace Poser.Game;

internal unsafe sealed class ActorSpawnNativeAdapter : IActorSpawnNativeAdapter, IDisposable
{
    // Brio ObjectMonitorService.cs: the native Character destructor. Hooking
    // it is what makes destruction stamps authoritative — Brio consumes the
    // same transition (OnCharacterDestroyed) to prune created indexes after
    // external deletion, and resolves the dying object's COM index inside
    // the callback exactly as we do.
    private const string CharacterFinalizeSig =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 81 C1";

    private delegate nint CharacterFinalizeDelegate(Character* chara);

    private readonly ClientObjectManagerNative _com = new();
    private readonly SpawnClientObjects _objects;
    private readonly Hook<CharacterFinalizeDelegate>? _finalizeHook;
    private readonly string? _lifetimeAuthorityDetail;

    public ActorSpawnNativeAdapter(
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog? log)
    {
        _objects = new SpawnClientObjects(_com);
        try
        {
            var address = sigScanner.ScanText(CharacterFinalizeSig);
            _finalizeHook = hooking.HookFromAddress<CharacterFinalizeDelegate>(
                address, CharacterFinalizeDetour);
            _finalizeHook.Enable();
        }
        catch (Exception ex)
        {
            _finalizeHook?.Dispose();
            _finalizeHook = null;
            _lifetimeAuthorityDetail =
                $"Character finalize hook unavailable: {ex.Message}";
            log?.Warning(
                $"ActorSpawnService: {_lifetimeAuthorityDetail} - spawning disabled");
        }
    }

    public bool IsAvailable => _com.IsAvailable;
    public bool IsLifetimeAuthoritative => _finalizeHook is not null;
    public string? LifetimeAuthorityDetail => _lifetimeAuthorityDetail;

    private nint CharacterFinalizeDetour(Character* chara)
    {
        try
        {
            var slot = _com.GetIndexByObject((nint)chara);
            _objects.Stamps.NoteDestroyed(
                (nint)chara,
                slot == SpawnClientObjects.NoIndex ? null : (ushort)slot);
        }
        catch
        {
            // Never fault the native destructor.
        }
        return _finalizeHook!.Original(chara);
    }

    public uint CreateBattleCharacter(byte reserveCompanionSlot) =>
        _objects.CreateBattleCharacter(reserveCompanionSlot);

    public ulong IndexDestructionStamp(ushort index) =>
        _objects.IndexDestructionStamp(index);

    public SpawnNativeDescriptor? ResolveByIndex(ushort index) =>
        _objects.ResolveByIndex(index);

    public SpawnNativeDescriptor? ResolveActor(nint address) =>
        _objects.ResolveActor(address);

    public bool DeleteExact(SpawnNativeDescriptor descriptor) =>
        _objects.DeleteExact(descriptor);

    /// <summary>Revalidates the exact descriptor and returns the live native
    /// object, or null when identity cannot be proven right now.</summary>
    private GameObject* Revalidate(SpawnNativeDescriptor descriptor)
    {
        var current = ResolveByIndex(descriptor.Index);
        return current is { } resolved && resolved == descriptor
            ? (GameObject*)descriptor.Address
            : null;
    }

    public bool EnableDraw(SpawnNativeDescriptor descriptor)
    {
        var gameObject = Revalidate(descriptor);
        if (gameObject is null)
            return false;
        gameObject->EnableDraw();
        return true;
    }

    /// <summary>
    /// Writes the character's alpha, which is how an actor is HIDDEN.
    ///
    /// <para>Not a draw-state write: <c>DisableDraw</c> tears the draw
    /// object down, and the skeleton — with the user's whole pose on it — goes
    /// with it, so re-showing rebuilt the actor standing in its animation's
    /// pose. Both references hide by fading instead, and both land on this
    /// same field: Brio writes <c>ExtendedAppearance.Transparency</c>
    /// (Capabilities/Actor/ActorAppearanceCapability.cs ToggleHide), Ktisis
    /// writes <c>CharacterEx-&gt;Opacity</c> (Scene/Entities/Game/
    /// ActorEntity.cs IsHidden). The draw object survives, so the pose does.
    /// </para>
    ///
    /// <para>The field's provenance is stated once, in
    /// <c>PresentationRuntimePort</c> (Brio's <c>Character.Alpha</c>,
    /// CS-named); this is the same field the Opacity slider drives, which is
    /// exactly the relationship both references have between their hide verb
    /// and their transparency control.</para>
    /// </summary>
    public bool SetAlpha(SpawnNativeDescriptor descriptor, float alpha)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        character->Alpha = Math.Clamp(alpha, 0f, 1f);
        return true;
    }

    public bool CopyDrawnAppearance(SpawnNativeDescriptor source, SpawnNativeDescriptor target)
    {
        var from = (Character*)Revalidate(source);
        var to = (Character*)Revalidate(target);
        if (from == null || to == null)
            return false;
        var drawn = from->GameObject.DrawObject;
        if (drawn == null
            || drawn->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase
            || ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawn)->GetModelType()
                != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
            return false;
        var human = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)drawn;
        to->DrawData.CustomizeData = human->Customize;
        var models = human->EquipmentModels;
        var slots = to->DrawData.EquipmentModelIds;
        for (int i = 0; i < models.Length && i < slots.Length; i++)
            slots[i] = models[i];
        // Facewear: its own two slots, not among the ten.
        var glasses = from->DrawData.GlassesIds;
        for (int i = 0; i < glasses.Length; i++)
            to->DrawData.SetGlasses(i, glasses[i]);
        var toDrawn = to->GameObject.DrawObject;
        if (toDrawn != null
            && toDrawn->Object.GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase
            && ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)toDrawn)->GetModelType()
                == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
        {
            // Already drawn (the once-posable pass): the drawn glasses models
            // straight across, the way the sync plugin set them.
            var toHuman = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)toDrawn;
            var sourceGlasses = human->GlassesModels;
            for (uint i = 0; i < (uint)sourceGlasses.Length; i++)
            {
                var model = sourceGlasses[(int)i];
                toHuman->SetGlassesSlotModel(i, &model);
            }
        }
        return true;
    }

    public bool CopyEquipmentVisibility(SpawnNativeDescriptor source, SpawnNativeDescriptor target)
    {
        var from = (Character*)Revalidate(source);
        var to = (Character*)Revalidate(target);
        if (from == null || to == null)
            return false;
        to->DrawData.HideWeapons(from->DrawData.IsWeaponHidden);
        to->DrawData.HideHeadgear(0, from->DrawData.IsHatHidden);
        to->DrawData.SetVisor(from->DrawData.IsVisorToggled);
        to->DrawData.HideVieraEars(from->DrawData.VieraEarsHidden);
        // The two flag bytes wholesale (+0x23E/+0x23F: the toggles above and
        // the rest — facewear visibility among them).
        *((byte*)&to->DrawData + 0x23E) = *((byte*)&from->DrawData + 0x23E);
        *((byte*)&to->DrawData + 0x23F) = *((byte*)&from->DrawData + 0x23F);
        return true;
    }

    public bool? IsReadyToDraw(SpawnNativeDescriptor descriptor)
    {
        var gameObject = Revalidate(descriptor);
        if (gameObject is null)
            return null;
        return gameObject->IsReadyToDraw();
    }

    public bool HasCompanionSlot(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        return character != null && character->ChildObject != null;
    }

    public bool TryReadCompanion(
        SpawnNativeDescriptor descriptor,
        out CompanionAttachment? attachment)
    {
        attachment = null;
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        attachment = ReadCompanionInfo(character);
        return true;
    }

    public nint ReadCompanionAddress(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return nint.Zero;
        // The child's GameObject is what the object table lists it by, which
        // is what an IActor's Address is.
        return (nint)(&character->ChildObject->GameObject);
    }

    public bool WriteCompanion(SpawnNativeDescriptor descriptor, CompanionKind kind, short id)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        switch (kind)
        {
            case CompanionKind.Companion:
                character->CompanionData.SetupCompanion(id, 0);
                break;
            case CompanionKind.Mount:
                character->Mount.CreateAndSetupMount(id, 0, 0, 0, 0, 0, 0);
                break;
            case CompanionKind.Ornament:
                character->OrnamentData.SetupOrnament(id, 0);
                break;
            default:
                return false;
        }
        return true;
    }

    public bool IsCompanionReady(SpawnNativeDescriptor descriptor, CompanionAttachment want)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return false;
        var info = ReadCompanionInfo(character);
        var native = &character->ChildObject->GameObject;
        return info == want && native->IsReadyToDraw();
    }

    public bool EnableCompanionDraw(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null || character->ChildObject == null)
            return false;
        character->ChildObject->GameObject.EnableDraw();
        return true;
    }

    public int? ReadModelCharaId(SpawnNativeDescriptor descriptor)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return null;
        return character->ModelContainer.ModelCharaId;
    }

    public bool WriteModelCharaIdAndBeginRedraw(SpawnNativeDescriptor descriptor, int modelCharaId)
    {
        var character = (Character*)Revalidate(descriptor);
        if (character == null)
            return false;
        // Brio's model change verbatim: write the id, then a full redraw —
        // draw down, wait for ready, draw up. The customize and equipment
        // bytes stay in DrawData behind a creature model, which is what makes
        // writing 0 later bring the human look back.
        character->ModelContainer.ModelCharaId = modelCharaId;
        character->GameObject.DisableDraw();
        return true;
    }

    private static CompanionAttachment? ReadCompanionInfo(Character* native)
    {
        if (native->ChildObject == null)
            return null;

        if (native->OrnamentData.OrnamentObject != null)
            return new CompanionAttachment(
                CompanionKind.Ornament, native->OrnamentData.OrnamentId);
        if (native->Mount.MountObject != null)
            return new CompanionAttachment(
                CompanionKind.Mount, (ushort)native->Mount.MountId);
        if (native->CompanionData.CompanionObject != null)
            return new CompanionAttachment(
                CompanionKind.Companion,
                (ushort)native->CompanionData.CompanionObject->Character.GameObject.BaseId);

        return null;
    }

    public void Dispose() => _finalizeHook?.Dispose();
}
