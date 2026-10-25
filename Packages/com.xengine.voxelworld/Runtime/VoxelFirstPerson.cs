using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>WASD + mouse look + left-break / right-place. Uses <see cref="CharacterController.Move"/>.</summary>
[AddComponentMenu("Voxel World/First Person")]
public sealed class VoxelFirstPerson : MonoBehaviour
{
    public VoxelWorld? World;
    public float MoveSpeed = 8f;
    public float LookSensitivity = 0.12f;
    public float MaxPitch = 89f;
    public float Reach = 8f;
    public ushort PlaceType = 4;
    public CharacterController? Controller;
    public int AttackDamage = 4;
    public readonly VoxelCharacterMotor Motor = new();
    public readonly VoxelCombat Combat = new();
    public readonly List<VoxelMob> Mobs = [];

    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    public override void Start()
    {
        Controller ??= GetComponent<CharacterController>();
        World ??= VoxelWorld.Active;
        Float3 euler = Transform.EulerAngles;
        Yaw = euler.Y;
        Pitch = euler.X;
        if (GetComponent<VoxelCharacterView>() == null)
            GameObject.AddComponent<VoxelCharacterView>();
        if (Mobs.Count == 0)
            SpawnDummyMob();
    }

    private void SpawnDummyMob()
    {
        var mob = new GameObject("DummyMob");
        if (World?.GameObject != null)
            mob.SetParent(World.GameObject, worldPositionStays: false);
        Float3 at = Transform.Position + new Float3(4.2f, 0f, 1.4f);
        mob.Transform.Position = at;
        var dummy = mob.AddComponent<VoxelDummyMob>();
        dummy.State.Position = at;
        Mobs.Add(dummy.State);
    }

    public override void Update()
    {
        World ??= VoxelWorld.Active;
        ApplyLook(Input.MouseDelta);
        ApplyMove(Input.GetWASD());
        if (World == null)
            return;
        World.Focus = Transform.Position;
        World.BreakFx.Tick(Time.DeltaTime);
        SelectHotbar();
        if (Input.GetKeyDown(KeyCode.F5))
            World.WriteSave();
        if (Input.GetKeyDown(KeyCode.F9))
            World.ReadSave();
        if (Input.GetMouseButtonDown(0) &&
            Combat.TryAttack(World, Mobs, EyeOrigin(), Transform.Forward, Reach, AttackDamage))
            VoxelSfx.PlayClick(GameObject);
        if (Input.GetMouseButtonDown(1))
        {
            ushort type = World.Inventory.SelectedType != 0 ? World.Inventory.SelectedType : PlaceType;
            if (World.TryPlace(EyeOrigin(), Transform.Forward, type, Reach))
                VoxelSfx.PlayClick(GameObject);
        }
    }

    public void SelectHotbar()
    {
        if (World == null)
            return;
        for (int i = 0; i < World.Inventory.Types.Length && i < 8; i++)
        {
            if (Input.GetKeyDown(KeyCode.Number1 + i))
                World.Inventory.Selected = i;
        }
    }

    public void ApplyLook(Float2 mouseDelta)
    {
        Yaw += mouseDelta.X * LookSensitivity;
        Pitch = Math.Clamp(Pitch - mouseDelta.Y * LookSensitivity, -MaxPitch, MaxPitch);
        if (GameObject != null)
            Transform.Rotation = Quaternion.FromEuler(Pitch, Yaw, 0f);
    }

    public void ApplyMove(Float2 wasd)
    {
        if (GameObject == null)
            return;
        Float3 forward = Transform.Forward;
        forward.Y = 0f;
        float flat = Float3.Length(forward);
        if (flat > 1e-4f)
            forward /= flat;
        Float3 right = Transform.Right;
        right.Y = 0f;
        float rightLen = Float3.Length(right);
        if (rightLen > 1e-4f)
            right /= rightLen;

        Motor.MoveSpeed = MoveSpeed;
        bool grounded = Controller != null && Controller.IsGrounded;
        Float3 motion = Motor.Step(
            wasd,
            Input.GetKeyDown(KeyCode.Space),
            grounded,
            Transform.Forward,
            Transform.Right,
            Time.DeltaTime);

        if (Controller != null)
            Controller.Move(motion);
        else
            Transform.Position += motion;
    }

    public Float3 EyeOrigin() => Transform.Position + new Float3(0f, 0.6f, 0f);
}
