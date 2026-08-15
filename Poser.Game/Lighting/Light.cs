using System.Numerics;
using Poser.Domain.Scene;
using Poser.Entities;
using PoserTransform = Poser.Transform;

namespace Poser.Game.Lighting;

/// <summary>
/// Plugin-side handle for one native scene light. Every accessor guards on
/// pointer validity: a getter falls back to the spawn default, a setter is a
/// silent no-op. Name is plugin state — the native light carries none.
/// </summary>
internal sealed unsafe class Light : ILight
{
    private GameLight* _native;

    public Light(
        GameLight* native,
        string name,
        LightOwnership ownership = LightOwnership.Spawned)
    {
        _native = native;
        Name = name;
        Ownership = ownership;
    }

    internal GameLight* NativePtr => _native;

    public LightOwnership Ownership { get; }

    public string? GoboPath { get; private set; }

    public IBone? AttachedBone { get; set; }

    /// <summary>The suppressed overworld original this light was copied from;
    /// zero for every other ownership.</summary>
    internal nint WorldOriginal { get; set; }

    /// <summary>GPose camera-light slot; -1 when not a GPose light.</summary>
    internal int GPoseSlot { get; set; } = -1;

    /// <summary>Gobo bookkeeping is the lighting service's alone — the entity
    /// exposes the path read-only so no caller can desync it from the native
    /// texture handle.</summary>
    internal void SetGoboPath(string? path) => GoboPath = path;

    public bool IsValid => _native != null;

    private bool HasRender => _native != null && _native->LightRenderObject != null;

    public string Name { get; set; }

    public LightKind Kind
    {
        get => HasRender
            ? ToKind(_native->LightRenderObject->EmissionType)
            : LightKind.Point;
        set
        {
            if (HasRender)
                _native->LightRenderObject->EmissionType = ToNative(value);
        }
    }

    public bool IsOn
    {
        get => IsValid && _native->VisibilityFlags != 0;
        set
        {
            if (IsValid)
                _native->VisibilityFlags = (byte)(value ? 79 : 0);
        }
    }

    public PoserTransform Transform
    {
        get => IsValid
            ? new PoserTransform(
                _native->Transform.Position,
                _native->Transform.Rotation,
                _native->Transform.Scale)
            : PoserTransform.Identity;
        set
        {
            if (!IsValid)
                return;
            _native->Transform.Position = value.Position;
            _native->Transform.Rotation = value.Rotation;
            _native->Transform.Scale = value.Scale;
        }
    }

    public Vector3 Color
    {
        get => HasRender ? _native->LightRenderObject->Color : new Vector3(20f);
        set
        {
            if (HasRender)
                _native->LightRenderObject->Color = value;
        }
    }

    public float Intensity
    {
        get => HasRender ? _native->LightRenderObject->Intensity : 1f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->Intensity = value;
        }
    }

    public float Range
    {
        get => HasRender ? _native->LightRenderObject->Range : 8f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->Range = value;
        }
    }

    public float Falloff
    {
        get => HasRender ? _native->LightRenderObject->Falloff : 1f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->Falloff = value;
        }
    }

    public LightFalloffType FalloffType
    {
        get => HasRender
            ? (LightFalloffType)(int)_native->LightRenderObject->FalloffType
            : LightFalloffType.Quadratic;
        set
        {
            if (HasRender)
                _native->LightRenderObject->FalloffType =
                    (FalloffType)(uint)(int)value;
        }
    }

    public float SpotAngle
    {
        get => HasRender ? _native->LightRenderObject->LightAngle : 45f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->LightAngle = value;
        }
    }

    public float FalloffAngle
    {
        get => HasRender ? _native->LightRenderObject->FalloffAngle : 0.5f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->FalloffAngle = value;
        }
    }

    public Vector2 AreaAngle
    {
        // The native field is RADIANS — both references drive it with
        // ImGui.SliderAngle and Ktisis multiplies by Rad2Deg before display.
        // The contract speaks degrees, so this boundary converts.
        get => HasRender
            ? new Vector2(
                float.RadiansToDegrees(
                    _native->LightRenderObject->AreaAngle.X),
                float.RadiansToDegrees(
                    _native->LightRenderObject->AreaAngle.Y))
            : Vector2.Zero;
        set
        {
            if (HasRender)
                _native->LightRenderObject->AreaAngle = new Vector2(
                    float.DegreesToRadians(value.X),
                    float.DegreesToRadians(value.Y));
        }
    }

    public bool HasReflection
    {
        get => HasFlag(LightFlags.Reflection);
        set => SetFlag(LightFlags.Reflection, value);
    }

    public bool CastsDynamicShadows
    {
        get => HasFlag(LightFlags.Dynamic);
        set => SetFlag(LightFlags.Dynamic, value);
    }

    public bool CastsCharacterShadow
    {
        get => HasFlag(LightFlags.CharaShadow);
        set => SetFlag(LightFlags.CharaShadow, value);
    }

    public bool CastsObjectShadow
    {
        get => HasFlag(LightFlags.ObjectShadow);
        set => SetFlag(LightFlags.ObjectShadow, value);
    }

    public float CharacterShadowRange
    {
        get => HasRender ? _native->LightRenderObject->CharacterShadowRange : 110f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->CharacterShadowRange = value;
        }
    }

    public float ShadowPlaneNear
    {
        get => HasRender ? _native->LightRenderObject->ShadowPlaneNear : 0.01f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->ShadowPlaneNear = value;
        }
    }

    public float ShadowPlaneFar
    {
        get => HasRender ? _native->LightRenderObject->ShadowPlaneFar : 17f;
        set
        {
            if (HasRender)
                _native->LightRenderObject->ShadowPlaneFar = value;
        }
    }

    /// <summary>Drops the native pointer after the light has been destroyed;
    /// every accessor degrades to its no-op form from here on.</summary>
    internal void Invalidate() => _native = null;

    private bool HasFlag(LightFlags flag) =>
        HasRender && (_native->LightRenderObject->LightFlags & flag) != 0;

    private void SetFlag(LightFlags flag, bool enabled)
    {
        if (!HasRender)
            return;
        if (enabled)
            _native->LightRenderObject->LightFlags |= flag;
        else
            _native->LightRenderObject->LightFlags &= ~flag;
    }

    internal static LightKind ToKind(LightType type) => type switch
    {
        LightType.WorldLight => LightKind.Directional,
        LightType.SpotLight => LightKind.Spot,
        LightType.FlatLight => LightKind.Area,
        _ => LightKind.Point,
    };

    internal static LightType ToNative(LightKind kind) => kind switch
    {
        LightKind.Directional => LightType.WorldLight,
        LightKind.Spot => LightType.SpotLight,
        LightKind.Area => LightType.FlatLight,
        _ => LightType.PointLight,
    };
}
