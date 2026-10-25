// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.InputSystem;

/// <summary>
/// Minimal demo consumer (zonezero M4 acceptance): translates its GameObject by the "Move"
/// action of the sibling <see cref="PlayerInput"/> each frame. Doubles as the reference
/// polling pattern for ported game code (Move value + button Triggered).
/// </summary>
[AddComponentMenu("Input/Action Mover (Demo)")]
public sealed class ActionMover : MonoBehaviour
{
    public float Speed = 2f;
    public string MoveAction = "Move";

    private InputAction? _move;
    private InputAction? _fire;

    public override void Start()
    {
        PlayerInput? playerInput = GameObject?.GetComponent<PlayerInput>();
        if (playerInput?.Actions == null) return;
        _move = playerInput.FindAction(MoveAction);
        _fire = playerInput.FindAction("Fire");
    }

    public override void Update()
    {
        if (_move == null) return;
        Float2 move = _move.ReadValue<Float2>();
        if (move != Float2.Zero && GameObject != null)
            GameObject.Transform.LocalPosition +=
                new Float3(move.X, 0f, move.Y) * (Speed * Time.DeltaTime);
        // Reference pattern only — keeps the Triggered polling compiled/visible.
        _ = _fire != null && _fire.Triggered();
    }
}
