using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Rotates the host transform (usually a directional light) for a day/night arc.</summary>
[AddComponentMenu("Voxel World/Day Cycle")]
public sealed class VoxelDayCycle : MonoBehaviour
{
    public float DegreesPerSecond = 6f;
    public float Yaw;

    public void Advance(float deltaSeconds)
    {
        Yaw = (Yaw + DegreesPerSecond * deltaSeconds) % 360f;
        if (GameObject != null)
            Transform.Rotation = Quaternion.FromEuler(50f, Yaw, 0f);
    }

    public override void Update() => Advance(Time.DeltaTime);
}
