using XEngine.Vector;

namespace XEngine.VoxelWorld;

public readonly struct VoxelBodyPart
{
    public readonly string Name;
    public readonly Float3 LocalPosition;
    public readonly Float3 Size;
    public readonly Color Color;

    public VoxelBodyPart(string name, Float3 localPosition, Float3 size, Color color)
    {
        Name = name;
        LocalPosition = localPosition;
        Size = size;
        Color = color;
    }
}

/// <summary>Owned cube-rig player / mob. Not Dungeon Mason, not Kronnect meshes.</summary>
public static class VoxelCharacterRig
{
    public static readonly Color Skin = new((byte)228, (byte)186, (byte)140);
    public static readonly Color Shirt = new((byte)48, (byte)118, (byte)188);
    public static readonly Color Pants = new((byte)70, (byte)78, (byte)140);
    public static readonly Color MobStone = new((byte)118, (byte)156, (byte)92);
    public static readonly Color MobEye = new((byte)80, (byte)240, (byte)90);

    public static VoxelBodyPart[] Player() =>
    [
        new("Head", new Float3(0f, 1.55f, 0f), new Float3(0.5f, 0.5f, 0.5f), Skin),
        new("Torso", new Float3(0f, 1.05f, 0f), new Float3(0.5f, 0.65f, 0.28f), Shirt),
        new("ArmL", new Float3(-0.38f, 1.08f, 0f), new Float3(0.18f, 0.58f, 0.18f), Skin),
        new("ArmR", new Float3(0.38f, 1.08f, 0f), new Float3(0.18f, 0.58f, 0.18f), Skin),
        new("LegL", new Float3(-0.14f, 0.4f, 0f), new Float3(0.22f, 0.72f, 0.22f), Pants),
        new("LegR", new Float3(0.14f, 0.4f, 0f), new Float3(0.22f, 0.72f, 0.22f), Pants),
    ];

    public static VoxelBodyPart[] Mob() =>
    [
        new("Head", new Float3(0f, 1.55f, 0.04f), new Float3(0.55f, 0.5f, 0.5f), MobStone),
        new("EyeL", new Float3(-0.12f, 1.58f, 0.28f), new Float3(0.1f, 0.1f, 0.06f), MobEye),
        new("EyeR", new Float3(0.12f, 1.58f, 0.28f), new Float3(0.1f, 0.1f, 0.06f), MobEye),
        new("Body", new Float3(0f, 0.85f, 0f), new Float3(0.55f, 0.9f, 0.35f), MobStone),
        new("LegFL", new Float3(-0.16f, 0.22f, 0.12f), new Float3(0.16f, 0.44f, 0.16f), MobStone),
        new("LegFR", new Float3(0.16f, 0.22f, 0.12f), new Float3(0.16f, 0.44f, 0.16f), MobStone),
        new("LegBL", new Float3(-0.16f, 0.22f, -0.12f), new Float3(0.16f, 0.44f, 0.16f), MobStone),
        new("LegBR", new Float3(0.16f, 0.22f, -0.12f), new Float3(0.16f, 0.44f, 0.16f), MobStone),
    ];
}
