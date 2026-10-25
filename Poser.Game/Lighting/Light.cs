using System.Numerics;
using Poser.Domain.Scene;
using Poser.Game.Entities;
using PoserTransform = Poser.Domain.Transforms.Transform;

namespace Poser.Game.Lighting;

/// <summary>
/// Handle for a native scene light. Native access checks the stored pointer and
/// an optional current-object callback. Unavailable native values use fallback
/// defaults; writes are ignored. Name and other bookkeeping remain managed state.
/// </summary>
internal sealed unsafe class Light : ILight
{
    private GameLight* _native;
    private readonly System.Func<bool>? _isCurrent;

    public Light(
        GameLight* native,
        string name,
        LightOwnership ownership = LightOwnership.Spawned,
        System.Func<bool>? isCurrent = null)
    {
        _native = native;
        Name = name;
        Ownership = ownership;
        _isCurrent = isCurrent;
    }

    internal GameLight* NativePtr => IsValid ? _native : null;

    public LightOwnership Ownership { get; }

    public string? GoboPath { get; private set; }

    public IBone? AttachedBone { get; set; }

    internal nint WorldAddress { get; set; }
    internal long WorldGeneration { get; set; }
    internal BorrowedLightState? WorldState { get; set; }

    /// <summary>GPose camera-light slot; -1 when not a GPose light.</summary>
    internal int GPoseSlot { get; set; } = -1;

    /// <summary>Updates the recorded texture path after native texture operations.</summary>
    internal void SetGoboPath(string? path) => GoboPath = path;

    public bool IsValid => _native != null && (_isCurrent?.Invoke() ?? true);

    private bool HasRender => IsValid && _native->LightRenderObject != null;

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
        get => IsValid && _native->IsVisible;
        set
        {
            // World lights now edit an owned replacement. On/off must not
            // destroy the authored intensity (including while hidden).
            if (IsValid)
                _native->IsVisible = value;
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
        // Convert between native radians and the public degree values.
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
