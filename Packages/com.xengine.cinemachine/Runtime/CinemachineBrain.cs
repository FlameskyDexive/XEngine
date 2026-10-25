// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Cinemachine;

/// <summary>Brain-side blend description (CM2 BlendDefinition).</summary>
[Serializable]
public sealed class BlendDefinition
{
    public BlendStyle Style = BlendStyle.EaseInOut;
    public float Time = 2f;

    public static BlendDefinition Cut() => new() { Style = BlendStyle.Cut, Time = 0f };
    public static BlendDefinition EaseInOut(float time) => new() { Style = BlendStyle.EaseInOut, Time = time };
}

/// <summary>
/// CM2 CinemachineBrain subset (zonezero M6): drives the sibling <see cref="Camera"/> from
/// the highest-priority live vcam each LateUpdate (after Animator — see ExecutionOrder),
/// blending between vcams with the default blend. The ZZZ big-skill cuts work exactly like
/// the reference: swap `DefaultBlend` (Cut ↔ EaseInOut) and toggle vcam GameObjects.
/// </summary>
[AddComponentMenu("Cinemachine/Cinemachine Brain")]
[ExecutionOrder(1000)]
public sealed class CinemachineBrain : MonoBehaviour
{
    [SerializeField] private BlendDefinition _defaultBlend = new() { Style = BlendStyle.EaseInOut, Time = 2f };

    private readonly List<ICinemachineVcam> _vcams = new();
    private ICinemachineVcam? _live;
    private CameraState _blendingFrom;
    private float _blendElapsed = -1f;
    private Camera? _camera;

    /// <summary>Blend applied when the live vcam changes (mutable at runtime — camera cuts).</summary>
    public BlendDefinition DefaultBlend
    {
        get => _defaultBlend;
        set => _defaultBlend = value;
    }

    public ICinemachineVcam? LiveVcam => _live;

    public override void LateUpdate()
    {
        if (GameObject == null) return;
        _camera ??= GameObject.GetComponent<Camera>();

        CinemachineSceneScan.Collect(_vcams);

        // Highest-priority live vcam (ties: first found — SetActive selection in the ZZZ rig
        // keeps exactly one live vcam anyway).
        ICinemachineVcam? next = null;
        foreach (ICinemachineVcam vcam in _vcams)
        {
            if (!vcam.IsLive) continue;
            if (next == null || vcam.Priority > next.Priority)
                next = vcam;
        }

        if (next == null) return;

        if (!ReferenceEquals(_live, next))
        {
            // Start a blend from the last applied state (camera stays put on the first frame).
            _blendingFrom = CurrentAppliedState();
            _blendElapsed = 0f;
            _live = next;
        }

        CameraState incoming = next.CalculateState(Time.DeltaTime);

        // Collider decoration: obstacle pull-in on the live vcam (CM2 extension pipeline —
        // the collider's own LateUpdate ran earlier in the frame and staged its correction).
        if (next is MonoBehaviour liveBehaviour && liveBehaviour.IsValid())
        {
            CinemachineCollider? collider = liveBehaviour.GetComponent<CinemachineCollider>();
            if (collider != null && next.LookAt != null)
                incoming.Position = collider.Correct(next.LookAt.Transform.Position, incoming.Position);
        }

        CameraState state = incoming;
        if (_blendElapsed >= 0f && _defaultBlend != null && _defaultBlend.Time > 0f)
        {
            _blendElapsed += Time.DeltaTime;
            float t = CinemachineBlend.Evaluate(_defaultBlend.Style, Math.Clamp(_blendElapsed / _defaultBlend.Time, 0f, 1f));
            state = CinemachineBlend.Lerp(_blendingFrom, incoming, t);
            if (_blendElapsed >= _defaultBlend.Time)
                _blendElapsed = -1f;
        }
        else
        {
            _blendElapsed = -1f;
        }

        Apply(state);
    }

    private CameraState CurrentAppliedState()
    {
        Transform transform = GameObject.Transform;
        return new CameraState
        {
            Position = transform.Position,
            Rotation = transform.Rotation,
            FieldOfView = _camera?.FieldOfView ?? 40f,
        };
    }

    private void Apply(CameraState state)
    {
        Transform transform = GameObject.Transform;
        transform.Position = state.Position;
        transform.Rotation = state.Rotation;
        if (_camera != null)
            _camera.FieldOfView = state.FieldOfView;
    }
}
