using System;
using System.Numerics;
using Poser.Core;
using Poser.Game.Structs;

namespace Poser.Entities;

/// <summary>
/// Entity representing a spawned scene light.
/// </summary>
public unsafe class LightEntity : EntityBase
{
    private GameLight* _nativePtr;

    /// <summary>
    /// The type of light.
    /// </summary>
    public LightType LightType { get; }

    /// <summary>
    /// Whether the native light pointer is valid.
    /// </summary>
    public bool IsValidLight => _nativePtr != null;

    /// <summary>
    /// Whether the light is currently on.
    /// </summary>
    public bool IsLightOn
    {
        get => IsValidLight && _nativePtr->LightFlags != 0;
        set
        {
            if (IsValidLight)
                _nativePtr->LightFlags = (byte)(value ? 79 : 0);
        }
    }

    public override EntityType EntityType => EntityType.Light;

    public override bool IsCollapsible => false;

    /// <summary>
    /// Gets the native light pointer for advanced operations.
    /// </summary>
    internal GameLight* NativePtr => _nativePtr;

    public LightEntity(EntityId id, string name, LightType lightType, GameLight* nativePtr)
        : base(id, name)
    {
        LightType = lightType;
        _nativePtr = nativePtr;
    }

    public override Transform Transform
    {
        get
        {
            if (!IsValidLight)
                return Transform.Identity;

            return new Transform(
                _nativePtr->Transform.Position,
                _nativePtr->Transform.Rotation,
                _nativePtr->Transform.Scale);
        }
        set
        {
            if (IsValidLight)
            {
                _nativePtr->Transform.Position = value.Position;
                _nativePtr->Transform.Rotation = value.Rotation;
                _nativePtr->Transform.Scale = value.Scale;
            }
        }
    }

    /// <summary>
    /// Light color (RGB, HDR values allowed).
    /// </summary>
    public Vector3 Color
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->Color
            : Vector3.One * 20f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->Color = value;
        }
    }

    /// <summary>
    /// Light intensity multiplier.
    /// </summary>
    public float Intensity
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->Intensity
            : 1f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->Intensity = value;
        }
    }

    /// <summary>
    /// Light range/radius.
    /// </summary>
    public float Range
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->Range
            : 35f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->Range = value;
        }
    }

    /// <summary>
    /// Light falloff power.
    /// </summary>
    public float Falloff
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->Falloff
            : 1f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->Falloff = value;
        }
    }

    /// <summary>
    /// Falloff type (Linear, Quadratic, Cubic).
    /// </summary>
    public FalloffType FalloffType
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->FalloffType
            : FalloffType.Quadratic;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->FalloffType = value;
        }
    }

    /// <summary>
    /// Spot light angle (degrees).
    /// </summary>
    public float SpotAngle
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->LightAngle
            : 45f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->LightAngle = value;
        }
    }

    /// <summary>
    /// Spot light falloff angle.
    /// </summary>
    public float FalloffAngle
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->FalloffAngle
            : 0.5f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->FalloffAngle = value;
        }
    }

    /// <summary>
    /// Whether the light has reflections enabled.
    /// </summary>
    public bool HasReflection
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            && (_nativePtr->LightRenderObject->LightFlags & LightFlags.Reflection) != 0;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
            {
                if (value)
                    _nativePtr->LightRenderObject->LightFlags |= LightFlags.Reflection;
                else
                    _nativePtr->LightRenderObject->LightFlags &= ~LightFlags.Reflection;
            }
        }
    }

    /// <summary>
    /// Whether the light casts character shadows.
    /// </summary>
    public bool CastsCharacterShadow
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            && (_nativePtr->LightRenderObject->LightFlags & LightFlags.CharaShadow) != 0;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
            {
                if (value)
                    _nativePtr->LightRenderObject->LightFlags |= LightFlags.CharaShadow;
                else
                    _nativePtr->LightRenderObject->LightFlags &= ~LightFlags.CharaShadow;
            }
        }
    }

    /// <summary>
    /// Character shadow range.
    /// </summary>
    public float CharacterShadowRange
    {
        get => IsValidLight && _nativePtr->LightRenderObject != null
            ? _nativePtr->LightRenderObject->CharacterShadowRange
            : 110f;
        set
        {
            if (IsValidLight && _nativePtr->LightRenderObject != null)
                _nativePtr->LightRenderObject->CharacterShadowRange = value;
        }
    }

    /// <summary>
    /// Invalidates the native pointer (call when light is destroyed).
    /// </summary>
    internal void Invalidate()
    {
        _nativePtr = null;
    }

    public override void Dispose()
    {
        _nativePtr = null;
        base.Dispose();
    }
}
