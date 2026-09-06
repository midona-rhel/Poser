using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;
using System.Numerics;
using System.Text;
using Transform = Poser.Transform;
using LayoutTransform = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform;

namespace Poser.Game.WorldObjects;

// A layout is NOT a Graphics.Scene.Object. Only this driver dereferences
// furniture addresses; the world port routes them before its BG/VFX resolver.
internal sealed unsafe class FurnitureLayoutDriver
{
    private readonly delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*> _create;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> _destroy;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance*, byte, void> _setupStain;
    private readonly IDataManager _data;
    private readonly Dictionary<nint, State> _owned = new();
    private readonly HashSet<nint> _known = new();
    private readonly HashSet<nint> _graphics = new();

    private sealed class State(WorldObjectIncarnation identity, Transform transform)
    {
        public readonly WorldObjectIncarnation Identity = identity;
        public Transform Transform = transform;
        public bool Visible = true;
        public float Opacity = 1f;
        public byte Stain;
        public Vector3? Tint;
        public bool Dirty = true;
    }

    internal FurnitureLayoutDriver(ISigScanner scanner, IDataManager data, IPluginLog log)
    {
        _data = data;
        try
        {
            _create = (delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*>)scanner.ScanText("E8 ?? ?? ?? ?? 48 89 04 ?? C6 44");
            _destroy = (delegate* unmanaged<SharedGroupLayoutInstance**, nint, void>)scanner.ScanText("48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 19 48 8B F9 48 8B F2");
            _setupStain = (delegate* unmanaged<SharedGroupLayoutInstance*, byte, void>)scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 8F ?? ?? ?? ?? 0F B6 47");
        }
        catch (Exception ex)
        {
            log.Warning($"Furniture natives unavailable; furniture spawns will refuse: {ex.Message}");
        }
    }

    internal bool Contains(nint address) => _owned.ContainsKey(address);
    internal bool IsLayoutAddress(nint address) => _known.Contains(address);
    internal bool OwnsGraphics(nint address) => _graphics.Contains(address);
    internal void RefreshGraphics()
    {
        _graphics.Clear();
        foreach (var address in _owned.Keys)
            CollectGraphics(&((SharedGroupLayoutInstance*)address)->Instances);
    }

    private void CollectGraphics(ChildNodeContainer* children)
    {
        foreach (var child in children->Instances)
        {
            if (child.Value == null || child.Value->Instance == null) continue;
            var instance = child.Value->Instance;
            _graphics.Add((nint)instance->GetGraphics());
            _graphics.Add((nint)instance->GetGraphics2());
            if (instance->Id.Type == InstanceType.SharedGroup)
                CollectGraphics(&((SharedGroupLayoutInstance*)instance)->Instances);
        }
    }
    internal void ForgetReusedGraphicsAddress(nint address) => _known.Remove(address);
    internal bool TryIdentity(nint address, out WorldObjectIncarnation identity)
    {
        identity = _owned.TryGetValue(address, out var state) ? state.Identity : default;
        return identity.Address != 0;
    }

    internal nint Spawn(string path, Transform transform, long generation, out WorldObjectIncarnation identity)
    {
        identity = default;
        if (_create == null || _destroy == null || _setupStain == null || !_data.FileExists(path)) return 0;
        var nativeTransform = ToNative(transform);
        var bytes = Encoding.UTF8.GetBytes(path + '\0');
        SharedGroupLayoutInstance* layout;
        fixed (byte* p = bytes)
            // Brio's SGLService: global layer -1, generated key 0, type 0xC
            // creates a standalone owned SGB, with no parent/bone attachment.
            layout = _create(-1, 0, &nativeTransform, p, null, 1, 0, 0xC, 0, 0);
        if (layout == null) return 0;
        var address = (nint)layout;
        identity = new(address, generation, 0);
        _known.Add(address);
        _owned.Add(address, new State(identity, transform));
        try
        {
            _setupStain(layout, 0);
            layout->SetTransformImpl(&nativeTransform);
        }
        catch
        {
            Destroy(address);
            throw;
        }
        return address;
    }

    internal bool Ready(nint address)
    {
        if (!Contains(address)) return false;
        var layout = (SharedGroupLayoutInstance*)address;
        // Brio FurnitureObject: vtable slot 22 is the layout/child readiness
        // query. A non-null allocation alone does not make graphics writable.
        var vtable = *(nint**)layout;
        return vtable != null && vtable[22] != 0
            && ((delegate* unmanaged<SharedGroupLayoutInstance*, byte>)vtable[22])(layout) != 0
            && (nint)layout->Instances.Instances.First != (nint)layout->Instances.Instances.Last;
    }

    internal Transform Read(nint address) => _owned[address].Transform;
    internal bool Visible(nint address) => _owned[address].Visible;
    internal float Opacity(nint address) => _owned[address].Opacity;

    internal void Write(nint address, Transform transform)
    {
        _owned[address].Transform = transform;
        var layout = (SharedGroupLayoutInstance*)address;
        var native = ToNative(transform);
        layout->SetTransformImpl(&native);
        if (Ready(address)) layout->Instances.ApplyTransforms();
    }

    internal void SetVisible(nint address, bool value)
    {
        _owned[address].Visible = value;
        ((SharedGroupLayoutInstance*)address)->SetActive(value);
        _owned[address].Dirty = true;
        Apply(address);
    }

    internal void SetOpacity(nint address, float value)
    {
        _owned[address].Opacity = value;
        _owned[address].Dirty = true;
        Apply(address);
    }

    internal bool SetColor(nint address, byte stain, Vector3? tint)
    {
        var state = _owned[address];
        state.Stain = stain;
        state.Tint = tint;
        state.Dirty = true;
        return Apply(address);
    }

    internal bool SetTint(nint address, Vector3? tint) => SetColor(address, _owned[address].Stain, tint);
    internal void Pump()
    {
        foreach (var (address, state) in _owned)
            if (state.Dirty) Apply(address);
    }

    private bool Apply(nint address)
    {
        if (!Ready(address)) return false;
        var state = _owned[address];
        var layout = (SharedGroupLayoutInstance*)address;
        layout->Instances.ApplyTransforms();
        layout->SetActive(state.Visible);
        ByteColor color;
        bool useColor = state.Tint is not null;
        if (state.Tint is { } tint)
            color = new ByteColor { R = Component(tint.X), G = Component(tint.Y), B = Component(tint.Z), A = 255 };
        else
        {
            color = new ByteColor { R = 255, G = 255, B = 255, A = 255 };
            if (layout->StainInfo == null || !layout->TryApplyStain(state.Stain))
            {
                byte stain = state.Stain;
                if (stain == 0 && layout->StainInfo != null) stain = layout->StainInfo->DefaultStainIndex;
                var nativeColor = SharedGroupLayoutInstance.GetObjectStainColorByIndex(stain);
                if (nativeColor != null) color = *nativeColor;
                useColor = true;
            }
        }
        ApplyChildren(&layout->Instances, 1f - state.Opacity, useColor ? &color : null);
        state.Dirty = false;
        return true;
    }

    private static void ApplyChildren(ChildNodeContainer* children, float transparency, ByteColor* color)
    {
        foreach (var child in children->Instances)
        {
            if (child.Value == null || child.Value->Instance == null) continue;
            var instance = child.Value->Instance;
            var first = instance->GetGraphics();
            var second = instance->GetGraphics2();
            ApplyTransparency(first, transparency);
            if (second != first) ApplyTransparency(second, transparency);
            if (color != null) instance->ApplyStain(color);
            if (instance->Id.Type == InstanceType.SharedGroup)
                ApplyChildren(&((SharedGroupLayoutInstance*)instance)->Instances, transparency, color);
        }
    }

    private static void ApplyTransparency(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object* graphics, float transparency)
    {
        if (graphics == null) return;
        // Layouts can contain lights as well as models. A light is not a DrawObject.
        if (graphics->GetObjectType() is not (ObjectType.BgObject or ObjectType.VfxObject or ObjectType.CharacterBase)) return;
        var draw = (DrawObject*)graphics;
        draw->SetTransparency(transparency);
        draw->UpdateMaterials();
        draw->UpdateCulling();
    }

    internal void Destroy(nint address)
    {
        if (!Contains(address)) return;
        var layout = (SharedGroupLayoutInstance*)address;
        // Brio's ordering: deinitialise the whole child graph, then release
        // the owning layout allocation. Never destroy its graphics separately.
        layout->Deinit();
        _destroy(&layout, 0);
        _owned.Remove(address);
        RefreshGraphics();
    }

    internal void Dispose()
    {
        foreach (var address in _owned.Keys.ToArray()) Destroy(address);
    }

    private static byte Component(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
    private static LayoutTransform ToNative(Transform value) => new() { Translation = value.Position, Rotation = value.Rotation, Scale = value.Scale };
}
