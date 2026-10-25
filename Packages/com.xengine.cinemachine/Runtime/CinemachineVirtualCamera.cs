// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Cinemachine;

/// <summary>CM2 Transposer binding modes the ZZZ rig uses.</summary>
public enum TransposerBindingMode
{
    /// <summary>Full target orientation frames the offset (shots' LockToTargetWithWorldUp is
    /// approximated by yaw-only framing — the reference targets are upright characters).</summary>
    LockToTargetWithWorldUp = 1,
    /// <summary>FreeLook orbit heading — target yaw + the FreeLook X axis.</summary>
    LockToTarget = 3,
}

/// <summary>
/// CM2 CinemachineTransposer subset: places the camera at follow-offset in the target's
/// (yaw-)rotated frame with per-axis damping. Attached to the vcam's GameObject or a child
/// (the reference's "cm" child layout works unchanged).
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine Transposer")]
public sealed class CinemachineTransposer : MonoBehaviour
{
    [SerializeField] private Float3 _followOffset = new(0f, 0.14f, -3.5f);
    [SerializeField] private float _dampingX = 1f;
    [SerializeField] private float _dampingY;
    [SerializeField] private float _dampingZ;

    /// <summary>External heading override (radians) — the FreeLook drives this.</summary>
    public float? HeadingOverride;

    private Float3 _dampedOffset;

    public Float3 FollowOffset
    {
        get => _followOffset;
        set => _followOffset = value;
    }

    public float XDamping
    {
        get => _dampingX;
        set => _dampingX = value;
    }

    public float YDamping
    {
        get => _dampingY;
        set => _dampingY = value;
    }

    public float ZDamping
    {
        get => _dampingZ;
        set => _dampingZ = value;
    }

    /// <summary>Computes the camera position for the follow target (damped over frames).</summary>
    public Float3 CalculatePosition(GameObject? follow, float deltaTime)
    {
        if (follow == null) return GameObject?.Transform.Position ?? Float3.Zero;

        float heading = HeadingOverride ?? Quaternion.ToEuler(follow.Transform.Rotation).Y;
        Float3 rotated = Quaternion.FromEuler(0f, heading, 0f) * _followOffset;
        Float3 desired = follow.Transform.Position + rotated;

        // First frame snaps; afterwards per-axis exponential damping (CM2 damping ≥ 0).
        if (_dampedOffset == Float3.Zero || deltaTime <= 0f)
        {
            _dampedOffset = desired;
        }
        else
        {
            _dampedOffset = new Float3(
                LerpAxis(_dampingX, _dampedOffset.X, desired.X, deltaTime),
                LerpAxis(_dampingY, _dampedOffset.Y, desired.Y, deltaTime),
                LerpAxis(_dampingZ, _dampedOffset.Z, desired.Z, deltaTime));
        }
        return _dampedOffset;
    }

    private static float LerpAxis(float damping, float from, float to, float dt)
    {
        // CM2: damping is a time constant; 0 → snap.
        if (damping <= 0f) return to;
        float k = 1f - MathF.Exp(-dt / damping * 10f);
        return from + (to - from) * Math.Clamp(k, 0f, 1f);
    }
}

/// <summary>
/// CM2 CinemachineComposer subset: aims the camera at the LookAt target (screen-center
/// placement — the ZZZ rigs center their targets with 0.5/0.5-ish screen offsets, which for
/// a look-at rotation is the identity framing) with horizontal/vertical damping.
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine Composer")]
public sealed class CinemachineComposer : MonoBehaviour
{
    [SerializeField] private Float3 _trackedObjectOffset = Float3.Zero;
    [SerializeField] private float _dampingHorizontal = 0.5f;
    [SerializeField] private float _dampingVertical = 0.5f;

    private Float3 _aimPoint;
    private bool _hasAim;

    public Float3 TrackedObjectOffset
    {
        get => _trackedObjectOffset;
        set => _trackedObjectOffset = value;
    }

    /// <summary>Damped aim rotation toward the LookAt target from the camera position.</summary>
    public Quaternion CalculateRotation(Float3 cameraPosition, GameObject? lookAt, float deltaTime)
    {
        if (lookAt == null) return Quaternion.Identity;
        Float3 target = lookAt.Transform.Position + _trackedObjectOffset;
        if (!_hasAim || deltaTime <= 0f)
        {
            _aimPoint = target;
            _hasAim = true;
        }
        else
        {
            float k = 1f - MathF.Exp(-deltaTime * 10f / MathF.Max(_dampingHorizontal, 0.01f));
            _aimPoint = Maths.Lerp(_aimPoint, target, Math.Clamp(k, 0f, 1f));
        }
        return Quaternion.LookRotation(_aimPoint - cameraPosition, Float3.UnitY);
    }
}

/// <summary>
/// CM2 CinemachineVirtualCamera subset: a Transposer + Composer pipeline on one GameObject
/// (or its children). Enabled GameObject = live; the brain picks by priority.
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine Virtual Camera")]
public sealed class CinemachineVirtualCamera : MonoBehaviour, ICinemachineVcam
{
    [SerializeField] private int _priority;
    [SerializeField] private GameObject? _follow;
    [SerializeField] private GameObject? _lookAt;
    [SerializeField] private float _fieldOfView = 40f;

    private CinemachineTransposer? _transposer;
    private CinemachineComposer? _composer;

    public int Priority
    {
        get => _priority;
        set => _priority = value;
    }

    public GameObject? Follow
    {
        get => _follow;
        set => _follow = value;
    }

    public GameObject? LookAt
    {
        get => _lookAt;
        set => _lookAt = value;
    }

    public float FieldOfView
    {
        get => _fieldOfView;
        set => _fieldOfView = value;
    }

    public bool IsLive => GameObject is { Enabled: true, EnabledInHierarchy: true };

    public CameraState CalculateState(float deltaTime)
    {
        _transposer ??= GameObject?.GetComponentInChildren<CinemachineTransposer>();
        _composer ??= GameObject?.GetComponentInChildren<CinemachineComposer>();

        Float3 position = _transposer?.CalculatePosition(_follow, deltaTime)
            ?? (_follow?.Transform.Position ?? Transform.Position);
        Quaternion rotation = _composer?.CalculateRotation(position, _lookAt, deltaTime)
            ?? Transform.Rotation;
        return new CameraState { Position = position, Rotation = rotation, FieldOfView = _fieldOfView };
    }
}
