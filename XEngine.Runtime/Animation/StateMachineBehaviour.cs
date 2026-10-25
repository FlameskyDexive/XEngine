// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// M36: user callback on an Animator state (Unity 5 StateMachineBehaviour analogue).
/// Instances are pre-created per Animator layer runtime and dispatched by index — no per-frame alloc.
/// </summary>
public abstract class StateMachineBehaviour
{
    public virtual void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex) { }
    public virtual void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex) { }
    public virtual void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex) { }
    public virtual void OnStateMove(Animator animator, AnimatorStateInfo stateInfo, int layerIndex) { }
}

/// <summary>Stock no-op behaviour so the Inspector Add Behaviour picker always has a built-in type.</summary>
public sealed class EmptyStateMachineBehaviour : StateMachineBehaviour
{
}
