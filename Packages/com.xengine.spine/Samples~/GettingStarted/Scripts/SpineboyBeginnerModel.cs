using System;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 4 model: movement / jump / aim / shoot state.</summary>
public class SpineboyBeginnerModel : MonoBehaviour
{
    public SpineBeginnerBodyState State;
    public bool FacingLeft;
    public float CurrentSpeed;
    public float ShootInterval = 0.12f;
    public float JumpTime = 1.2f;
    public float JumpPower = 20f;

    public event Action? ShootEvent;
    public event Action? StartAimEvent;
    public event Action? StopAimEvent;

    private float _lastShootTime = -999f;
    private bool _jumping;

    public void TryShoot()
    {
        float now = Time.TimeSinceStartup;
        if (now - _lastShootTime <= ShootInterval)
            return;
        _lastShootTime = now;
        ShootEvent?.Invoke();
    }

    public void StartAim() => StartAimEvent?.Invoke();

    public void StopAim() => StopAimEvent?.Invoke();

    public void TryMove(float speed)
    {
        CurrentSpeed = speed;
        if (speed != 0f)
            FacingLeft = speed < 0f;
        if (State != SpineBeginnerBodyState.Jumping)
            State = speed == 0f ? SpineBeginnerBodyState.Idle : SpineBeginnerBodyState.Running;
    }

    public void TryJump()
    {
        if (_jumping || State == SpineBeginnerBodyState.Jumping)
            return;
        JumpRoutine().Forget();
    }

    private async XTaskVoid JumpRoutine()
    {
        _jumping = true;
        State = SpineBeginnerBodyState.Jumping;
        Float3 pos = Transform.LocalPosition;
        float half = JumpTime * 0.5f;
        for (float t = 0; t < half && this.IsValid(); t += Time.DeltaTime)
        {
            float d = JumpPower * (half - t);
            Transform.Translate(new Float3(0f, d * Time.DeltaTime, 0f));
            await XTask.NextFrame();
        }

        for (float t = 0; t < half && this.IsValid(); t += Time.DeltaTime)
        {
            float d = JumpPower * t;
            Transform.Translate(new Float3(0f, -d * Time.DeltaTime, 0f));
            await XTask.NextFrame();
        }

        if (this.IsValid())
            Transform.LocalPosition = pos;
        State = SpineBeginnerBodyState.Idle;
        _jumping = false;
    }
}
