using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Stone dummy target. LMB prefers this AABB over terrain.</summary>
[AddComponentMenu("Voxel World/Dummy Mob")]
public sealed class VoxelDummyMob : MonoBehaviour
{
    public int Health = 20;
    public VoxelMob State { get; } = new();

    public override void Start()
    {
        State.Health = Health;
        State.MaxHealth = Health;
        State.Position = Transform.Position;
        if (GameObject.ChildCount == 0)
            VoxelCharacterView.Attach(GameObject, VoxelCharacterRig.Mob());
    }

    public override void Update()
    {
        Transform.Position = State.Position;
        if (!State.IsAlive)
            GameObject.Enabled = false;
    }
}
