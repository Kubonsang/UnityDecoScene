using System;

namespace UnityDecoScene.DungeonDecorator
{
    public enum DecorAssetType
    {
        Prop,
        Light,
        Decal
    }

    public enum DecorRole
    {
        Hero,
        Support,
        StoryEvidence,
        Clutter,
        LightingCue,
        DecalCue
    }

    public enum PlacementSurface
    {
        Floor,
        Wall,
        Ceiling,
        Any
    }

    public enum ContactRequirement
    {
        FreeStanding,
        FloorSupported,
        WallBacked,
        WallMounted,
        CeilingMounted
    }

    public enum GeometrySource
    {
        Unknown,
        Collider,
        RendererBounds,
        Mixed,
        Manual
    }

    public enum RoomSurfaceType
    {
        Floor,
        Wall,
        Ceiling
    }

    public enum CompositionRelation
    {
        Independent,
        Surrounds,
        Faces,
        Supports,
        ScatteredNear,
        AttachedTo,
        Avoids
    }

    public enum PreferredZone
    {
        Any,
        Focal,
        Center,
        Perimeter,
        Corner
    }

    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    [Flags]
    public enum RotationMode
    {
        Fixed = 0,
        QuarterTurns = 1,
        FaceAnchor = 2,
        RandomYaw = 4
    }
}
