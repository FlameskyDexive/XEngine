using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 5: Hero platformer using CharacterController + animation handle.</summary>
public class BasicPlatformerController : MonoBehaviour
{
    public enum CharacterState
    {
        None,
        Idle,
        Walk,
        Run,
        Crouch,
        Rise,
        Fall,
        Attack
    }

    public CharacterController? Controller;
    public SkeletonAnimationHandleExample? AnimationHandle;
    public float WalkSpeed = 1.5f;
    public float RunSpeed = 7f;
    public float GravityScale = 6.6f;
    public float JumpSpeed = 25f;
    public float MinimumJumpDuration = 0.5f;
    public float JumpInterruptFactor = 0.5f;

    public CharacterState CurrentState { get; private set; } = CharacterState.Idle;
    public Float3 Velocity;

    private float _minimumJumpEndTime;
    private bool _wasGrounded = true;

    public override void Start()
    {
        Controller ??= GetComponent<CharacterController>();
        AnimationHandle ??= GetComponent<SkeletonAnimationHandleExample>();
    }

    public override void Update()
    {
        float dt = Time.DeltaTime;
        bool grounded = Controller?.IsGrounded ?? _wasGrounded;
        bool landed = !_wasGrounded && grounded;

        float inputX = 0f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.Left))
            inputX -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.Right))
            inputX += 1f;
        float inputY = 0f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.Down))
            inputY -= 1f;
        bool jumpStart = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W);
        bool jumpStop = Input.GetKeyUp(KeyCode.Space) || Input.GetKeyUp(KeyCode.W);
        bool crouch = grounded && inputY < -0.5f;

        ApplyMotion(dt, grounded, inputX, crouch, jumpStart, jumpStop);
        CharacterState next = ResolveState(grounded, crouch, inputX);
        if (next != CurrentState)
        {
            CurrentState = next;
            AnimationHandle?.PlayAnimationForState(StateName(next), 0);
        }

        if (inputX != 0f)
            AnimationHandle?.SetFlip(inputX);
        _wasGrounded = grounded;
        _ = landed;
    }

    public void ApplyMotion(float dt, bool grounded, float inputX, bool crouch, bool jumpStart, bool jumpStop)
    {
        bool jumpInterrupt = !grounded && jumpStop && Time.TimeSinceStartup < _minimumJumpEndTime;
        if (grounded && jumpStart && !crouch)
        {
            Velocity.Y = JumpSpeed;
            _minimumJumpEndTime = Time.TimeSinceStartup + MinimumJumpDuration;
        }
        else if (jumpInterrupt && Velocity.Y > 0f)
            Velocity.Y *= JumpInterruptFactor;

        Velocity.X = 0f;
        if (!crouch && inputX != 0f)
        {
            Velocity.X = Math.Abs(inputX) > 0.6f ? RunSpeed : WalkSpeed;
            Velocity.X *= Math.Sign(inputX);
        }

        if (!grounded)
            Velocity.Y += -9.81f * GravityScale * dt;
        else if (Velocity.Y < 0f)
            Velocity.Y = 0f;

        Controller?.Move(Velocity * dt);
    }

    public static string StateName(CharacterState state) => state switch
    {
        CharacterState.Walk => "walk",
        CharacterState.Run => "run",
        CharacterState.Crouch => "crouch",
        CharacterState.Rise => "rise",
        CharacterState.Fall => "fall",
        CharacterState.Attack => "attack",
        _ => "idle"
    };

    private static CharacterState ResolveState(bool grounded, bool crouch, float inputX)
    {
        if (grounded)
        {
            if (crouch) return CharacterState.Crouch;
            if (inputX == 0f) return CharacterState.Idle;
            return Math.Abs(inputX) > 0.6f ? CharacterState.Run : CharacterState.Walk;
        }

        return CharacterState.Rise;
    }
}
