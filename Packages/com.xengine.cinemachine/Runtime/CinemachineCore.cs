// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Cinemachine;

/// <summary>Resolved camera pose a vcam hands to the brain (CM2 CameraState subset).</summary>
public struct CameraState
{
    public Float3 Position;
    public Quaternion Rotation;
    public float FieldOfView;
}

/// <summary>Blend ease curves (CM2 CinemachineBlend.Styles).</summary>
public enum BlendStyle
{
    Cut = 0,
    EaseInOut = 1,
    EaseIn = 2,
    EaseOut = 3,
    Linear = 6,
}

/// <summary>Anything the brain can treat as a live virtual camera.</summary>
public interface ICinemachineVcam
{
    int Priority { get; }

    /// <summary>Look-at target (the collider raycasts from it toward the camera).</summary>
    GameObject? LookAt { get; }
    /// <summary>Enabled-and-eligible (a disabled GameObject deactivates the vcam, the ZZZ
    /// big-skill shots are selected purely by SetActive).</summary>
    bool IsLive { get; }
    /// <summary>Advances internal damping/orbit state by dt and returns the camera pose.</summary>
    CameraState CalculateState(float deltaTime);
}

/// <summary>Shared blend math + ease curves.</summary>
public static class CinemachineBlend
{
    public static float Evaluate(BlendStyle style, float t)
    {
        return style switch
        {
            BlendStyle.Cut => 1f,
            BlendStyle.Linear => t,
            BlendStyle.EaseIn => t * t,
            BlendStyle.EaseOut => 1f - (1f - t) * (1f - t),
            // Smoothstep — CM2's EaseInOut.
            _ => t * t * (3f - 2f * t),
        };
    }

    public static CameraState Lerp(CameraState a, CameraState b, float t)
    {
        return new CameraState
        {
            Position = Maths.Lerp(a.Position, b.Position, t),
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
            FieldOfView = a.FieldOfView + (b.FieldOfView - a.FieldOfView) * t,
        };
    }
}

/// <summary>
/// Scene scan helper: collects every live vcam component under the current scene's roots.
/// Demo-sized scenes only (a few hundred objects) — matches how the brain picks a vcam.
/// </summary>
internal static class CinemachineSceneScan
{
    public static void Collect(List<ICinemachineVcam> results)
    {
        results.Clear();
        Scene? scene = Scene.Current;
        if (scene == null) return;
        foreach (GameObject root in scene.RootObjects)
            Walk(root.Transform, results);
    }

    private static void Walk(Transform transform, List<ICinemachineVcam> results)
    {
        // GetComponent<T> is MonoBehaviour-constrained; the interface lookup goes through the
        // Type overload (assignability-based).
        if (transform.GameObject.GetComponent(typeof(ICinemachineVcam)) is ICinemachineVcam vcam)
            results.Add(vcam);
        for (int i = 0; i < transform.ChildCount; i++)
            Walk(transform.GetChild(i), results);
    }
}

/// <summary>CM2 axis value (normalized 0..1 for the FreeLook Y, degrees for X) with the
/// clamping/wrapping rules the ZZZ rig uses.</summary>
[Serializable]
public sealed class CinemachineAxis
{
    public float Value;
    public float Min;
    public float Max;
    public bool Wrap;

    public float Clamp(float value)
    {
        if (Wrap && Max - Min > 0.01f)
        {
            float range = Max - Min;
            value = ((value - Min) % range + range) % range + Min;
            return value;
        }
        return Math.Clamp(value, Min, Max);
    }
}
