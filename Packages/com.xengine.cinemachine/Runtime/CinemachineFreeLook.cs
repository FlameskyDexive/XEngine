// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Cinemachine;

/// <summary>One FreeLook orbit ring (CM2 FreeLook.Orbit).</summary>
[Serializable]
public sealed class FreeLookOrbit
{
    public float Height;
    public float Radius;
}

/// <summary>
/// CM2 CinemachineFreeLook subset (zonezero M6): a single vcam whose camera rides a
/// 3-rig orbit (Top/Middle/Bottom) around the Follow target. The Y axis (0..1) blends
/// between the rigs (spline-smoothed height/radius), the X axis (degrees, ±180 wrap) orbits
/// around the target's yaw heading (Heading = TargetForward, like the ZZZ reference).
/// Aim keeps the target centered (the reference Composers all sit at ~0.5/0.5).
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine FreeLook")]
public sealed class CinemachineFreeLook : MonoBehaviour, ICinemachineVcam
{
    [SerializeField] private int _priority = 10;
    [SerializeField] private GameObject? _follow;
    [SerializeField] private GameObject? _lookAt;
    [SerializeField] private float _fieldOfView = 40f;
    [SerializeField] private float _splineCurvature = 0.2f;

    [SerializeField] private FreeLookOrbit _top = new() { Height = 1.47f, Radius = 2.3f };
    [SerializeField] private FreeLookOrbit _middle = new() { Height = 0.14f, Radius = 3.5f };
    [SerializeField] private FreeLookOrbit _bottom = new() { Height = -1.1f, Radius = 2.5f };

    /// <summary>Orbit heading in degrees around the target (reference: ±180, wrap, MaxSpeed 1.5).</summary>
    public CinemachineAxis XAxis = new() { Min = -180f, Max = 180f, Wrap = true };

    /// <summary>Vertical frame position 0=Bottom rig, 0.5=Middle, 1=Top (reference default 0.5).</summary>
    public CinemachineAxis YAxis = new() { Min = 0f, Max = 1f };

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

    public FreeLookOrbit Top
    {
        get => _top;
        set => _top = value;
    }

    public FreeLookOrbit Middle
    {
        get => _middle;
        set => _middle = value;
    }

    public FreeLookOrbit Bottom
    {
        get => _bottom;
        set => _bottom = value;
    }

    public bool IsLive => GameObject is { Enabled: true, EnabledInHierarchy: true };

    public CameraState CalculateState(float deltaTime)
    {
        if (_follow == null)
            return new CameraState { Position = Transform.Position, Rotation = Transform.Rotation, FieldOfView = _fieldOfView };

        // Spline-blended orbit profile at the current Y (CM2 interpolates the three rigs with
        // the spline curvature; the smoothstep blend matches its shape closely for demo use).
        float y = YAxis.Clamp(YAxis.Value);
        float height, radius;
        if (y >= 0.5f)
        {
            float t = Smooth((y - 0.5f) * 2f);
            height = _middle.Height + (_top.Height - _middle.Height) * t;
            radius = _middle.Radius + (_top.Radius - _middle.Radius) * t;
        }
        else
        {
            float t = Smooth(y * 2f);
            height = _bottom.Height + (_middle.Height - _bottom.Height) * t;
            radius = _bottom.Radius + (_middle.Radius - _bottom.Radius) * t;
        }

        // Heading = target yaw (TargetForward) + user orbit (X axis, degrees — ToEuler is degrees).
        float targetYaw = Quaternion.ToEuler(_follow.Transform.Rotation).Y;
        float headingDegrees = targetYaw + XAxis.Clamp(XAxis.Value);
        float heading = headingDegrees * MathF.PI / 180f;

        Float3 target = _follow.Transform.Position;
        Float3 orbitCenter = target + Float3.UnitY * height;
        // Behind the target along the heading (Unity orbital transposer's -Z offset): forward
        // for a yaw angle is (sin, 0, cos), so the camera sits at center - forward * radius.
        Float3 position = orbitCenter - new Float3(MathF.Sin(heading) * radius, 0f, MathF.Cos(heading) * radius);

        GameObject? lookAt = _lookAt ?? _follow;
        Quaternion rotation = Quaternion.LookRotation(lookAt.Transform.Position - position, Float3.UnitY);
        return new CameraState { Position = position, Rotation = rotation, FieldOfView = _fieldOfView };
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
}
