// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>M36: StateMachineBehaviour compile + zero-alloc enter/update/exit dispatch.</summary>
public class AnimatorControllerBehaviourTests : RuntimeTestBase
{
    public sealed class CountingBehaviour : StateMachineBehaviour
    {
        public static int Enters;
        public static int Updates;
        public static int Exits;
        public static int Moves;

        public static void Reset()
        {
            Enters = Updates = Exits = Moves = 0;
        }

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
            => Enters++;

        public override void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
            => Updates++;

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
            => Exits++;

        public override void OnStateMove(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
            => Moves++;
    }

    private static string TypeName => typeof(CountingBehaviour).AssemblyQualifiedName!;

    private static AnimatorController BuildIdleAttackAuthor()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", 10f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter
        {
            Name = "Go", Type = AnimatorParameterType.Trigger,
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Motion = new AssetRef<AnimationClip>(idle),
            TransitionNames = { "ToAttack" },
            Behaviours = { TypeName },
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Attack",
            Motion = new AssetRef<AnimationClip>(attack),
            IsLooping = false,
            Behaviours = { TypeName },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "ToAttack",
            DestinationStateName = "Attack",
            Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Go" } },
        });
        return author;
    }

    private (Scene scene, Animator animator, AnimatorControllerRuntime runtime)
        Build(AnimatorControllerConstant constant)
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(constant);
        return (scene, animator, runtime);
    }

    [Fact]
    public void Compile_CopiesBehaviourTypeNames()
    {
        var c = AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor());
        Assert.Equal(2, c.States.Length);
        Assert.Single(c.BehaviourTypeNames);
        Assert.Contains("CountingBehaviour", c.BehaviourTypeNames[0]);
        Assert.Equal(1, c.States[0].BehavioursCount);
        Assert.Equal(1, c.States[1].BehavioursCount);
        Assert.Equal(c.States[0].BehavioursStart + 1, c.States[1].BehavioursStart);
        Assert.Equal(0, c.BehaviourTypeIndices[c.States[0].BehavioursStart]);
        Assert.Equal(0, c.BehaviourTypeIndices[c.States[1].BehavioursStart]);
    }

    [Fact]
    public void FirstEvaluate_DispatchesEnterThenUpdate()
    {
        CountingBehaviour.Reset();
        var (scene, _, runtime) = Build(AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor()));
        Assert.NotNull(runtime.GetBehaviour(0, 0));
        Update(scene, frames: 1);
        Assert.Equal(1, CountingBehaviour.Enters);
        Assert.Equal(1, CountingBehaviour.Updates);
        Assert.Equal(1, CountingBehaviour.Moves);
        Assert.Equal(0, CountingBehaviour.Exits);
        Assert.Equal(0, runtime.CurrentStateIndex);
    }

    [Fact]
    public void LaterFrames_DispatchUpdateWithoutReenter()
    {
        CountingBehaviour.Reset();
        var (scene, _, _) = Build(AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor()));
        Update(scene, frames: 3);
        Assert.Equal(1, CountingBehaviour.Enters);
        Assert.Equal(3, CountingBehaviour.Updates);
        Assert.Equal(3, CountingBehaviour.Moves);
        Assert.Equal(0, CountingBehaviour.Exits);
    }

    [Fact]
    public void Transition_DispatchesExitThenEnter()
    {
        CountingBehaviour.Reset();
        var (scene, animator, runtime) = Build(AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor()));
        Update(scene, frames: 1);
        animator.SetTrigger("Go");
        Update(scene, frames: 1);
        Assert.Equal(1, runtime.CurrentStateIndex);
        Assert.Equal(2, CountingBehaviour.Enters);
        Assert.Equal(1, CountingBehaviour.Exits);
        Assert.Equal(2, CountingBehaviour.Updates);
        Assert.Equal(2, CountingBehaviour.Moves);
    }

    [Fact]
    public void ReloadBehaviours_RecreatesInstances()
    {
        CountingBehaviour.Reset();
        var (_, _, runtime) = Build(AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor()));
        var first = runtime.GetBehaviour(0, 0);
        Assert.NotNull(first);
        runtime.ReloadBehaviours();
        var second = runtime.GetBehaviour(0, 0);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}
