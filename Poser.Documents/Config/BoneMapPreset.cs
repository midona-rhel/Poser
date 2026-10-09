using System;
using System.Collections.Generic;
using Poser.Domain.Posing;

namespace Poser.Config;

public enum BoneMapKind { Body, Face }

/// <summary>A reusable UI layout, independent of actor and skeleton generations.</summary>
public sealed class BoneMapPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public BoneMapKind Kind { get; set; }
    public List<BoneMapPoint> Points { get; set; } = new();
}

/// <summary>Normalized map coordinates; section identity bounds visual connectors.</summary>
public sealed record BoneMapPoint(PortableBoneId Bone, string Section, float X, float Y);
