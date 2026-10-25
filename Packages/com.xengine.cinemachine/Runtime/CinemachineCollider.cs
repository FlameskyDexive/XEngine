// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Cinemachine;

/// <summary>
/// CM2 CinemachineCollider subset (zonezero M6): pulls the camera toward the target when
/// geometry (CollideAgainst layers) blocks the line of sight. Pure correction function
/// invoked by the <see cref="CinemachineBrain"/> after the vcam state is computed — the
/// raycast runs from the LookAt target toward the desired camera position; on a hit the
/// camera moves to the hit point pulled back by CameraRadius, never closer than
/// MinimumDistanceFromTarget. Attach next to the vcam it decorates (FreeLook included).
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine Collider")]
public sealed class CinemachineCollider : MonoBehaviour
{
    [SerializeField] private LayerMask _collideAgainst = LayerMask.Everything;
    [SerializeField] private float _cameraRadius = 0.1f;
    [SerializeField] private float _minimumDistanceFromTarget = 0.1f;

    public LayerMask CollideAgainst
    {
        get => _collideAgainst;
        set => _collideAgainst = value;
    }

    /// <summary>Applies obstacle pull-in to a computed camera state (raycast-based).</summary>
    public Float3 Correct(Float3 lookAtTarget, Float3 desiredPosition)
    {
        Float3 toCamera = desiredPosition - lookAtTarget;
        float desiredDistance = Float3.Length(toCamera);
        if (desiredDistance < 1e-4f) return desiredPosition;

        Float3 direction = toCamera * (1f / desiredDistance);
        if (Physics.Raycast(lookAtTarget, direction, out RaycastHit hit, desiredDistance, _collideAgainst))
        {
            float allowed = MathF.Max(hit.Distance - _cameraRadius, _minimumDistanceFromTarget);
            if (allowed < desiredDistance)
                return lookAtTarget + direction * allowed;
        }
        return desiredPosition;
    }
}
