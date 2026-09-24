using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using NativeTransform = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace Poser.Game.Lighting;

/// <summary>Source values for an editable replacement; only visibility is changed on the original.</summary>
internal sealed unsafe class BorrowedLightState : IDisposable
{
    private readonly NativeTransform _transform;
    private readonly byte _visibility;
    private readonly LightFlags _flags;
    private readonly LightType _type;
    private readonly Vector4 _color;
    private readonly float _near, _far, _falloff, _angle, _falloffAngle, _range, _shadowRange;
    private readonly FalloffType _falloffType;
    private readonly Vector2 _areaAngle;
    private readonly nint _renderTexture;
    private readonly Action<nint> _releaseTexture;
    private nint _texture;
    private bool _released;
    private bool _copied;

    internal BorrowedLightState(GameLight* native, Action<nint>? retainTexture = null, Action<nint>? releaseTexture = null)
    {
        var render = native->LightRenderObject;
        _transform = native->Transform;
        _visibility = native->VisibilityFlags;
        _flags = render->LightFlags;
        _type = render->EmissionType;
        _color = render->ColorIntensity;
        _near = render->ShadowPlaneNear; _far = render->ShadowPlaneFar;
        _falloffType = render->FalloffType; _areaAngle = render->AreaAngle;
        _falloff = render->Falloff; _angle = render->LightAngle;
        _falloffAngle = render->FalloffAngle; _range = render->Range;
        _shadowRange = render->CharacterShadowRange;
        _renderTexture = (nint)render->Texture;
        _releaseTexture = releaseTexture ?? (p => ((TextureResourceHandle*)p)->DecRef());
        var texture = (nint)native->ProjectedCubemapTexture;
        // The replacement receives its own texture reference. Never copy the
        // native object's ownership/vtable or its render transform pointer.
        if (texture != 0)
        {
            (retainTexture ?? (p => ((TextureResourceHandle*)p)->IncRef()))(texture);
            _texture = texture;
        }
    }

    internal void CopyTo(GameLight* native)
    {
        if (_released || _copied || native == null || native->LightRenderObject == null)
            throw new InvalidOperationException("The world light replacement is not ready.");
        native->Transform = _transform;
        native->VisibilityFlags = _visibility;
        if (native->ProjectedCubemapTexture != null)
            _releaseTexture((nint)native->ProjectedCubemapTexture);
        native->ProjectedCubemapTexture = (TextureResourceHandle*)_texture;
        _texture = 0;
        var render = native->LightRenderObject;
        if (render != null)
        {
            render->Transform = &native->Transform;
            render->LightFlags = _flags; render->EmissionType = _type;
            render->ColorIntensity = _color;
            render->ShadowPlaneNear = _near; render->ShadowPlaneFar = _far;
            render->FalloffType = _falloffType; render->AreaAngle = _areaAngle;
            render->Falloff = _falloff; render->LightAngle = _angle;
            render->FalloffAngle = _falloffAngle; render->Range = _range;
            render->CharacterShadowRange = _shadowRange;
            render->Texture = (void*)_renderTexture;
        }
        _copied = true;
    }

    internal void Suppress(GameLight* original)
    {
        if (!_released && original != null)
            original->IsVisible = false;
    }

    internal bool Restore(GameLight* original)
    {
        if (_released || original == null) return false;
        original->VisibilityFlags = _visibility;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_released) return;
        if (_texture != 0) _releaseTexture(_texture);
        _texture = 0;
        _released = true;
    }
}
